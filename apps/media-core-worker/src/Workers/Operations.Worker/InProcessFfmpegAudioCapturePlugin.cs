using System.Buffers;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class InProcessFfmpegAudioCapturePlugin : IAudioCapturePlugin, IDisposable
{
    private const int AudioSampleRate = 16000;
    private const int AudioChannels = 1;
    private const int AudioBytesPerSample = 2;
    private static readonly int TranscriptionChunkOverlapSeconds = ResolveConfiguredInt("MEDIA_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS", 8, 0, 15);
    private static readonly int RecognitionWindowSeconds = ResolveConfiguredInt("MEDIA_RECOGNITION_WINDOW_SECONDS", 12, 4, 30);
    private static readonly int RecognitionWindowOverlapSeconds = ResolveConfiguredInt("MEDIA_RECOGNITION_WINDOW_OVERLAP_SECONDS", 4, 0, 10);
    private static readonly bool RecognitionDualPass = ResolveBoolEnv("MEDIA_RECOGNITION_DUAL_PASS", defaultValue: false);
    // How many seconds before/after the target window cut to search for a silence boundary.
    // 0 disables VAD-aligned cuts (falls back to fixed-time windows).
    private static readonly int RecognitionVadSearchSeconds = ResolveConfiguredInt("MEDIA_RECOGNITION_VAD_SEARCH_SECONDS", 2, 0, 5);
    // How many VAD windows to accumulate before writing a single JSON entry.
    // e.g. 3 windows × 12-15 s each = ~36-45 s per JSON entry.
    private static readonly int WindowsPerEntry = ResolveConfiguredInt("MEDIA_WINDOWS_PER_ENTRY", 3, 1, 10);
    // 20 ms analysis frame at 16 kHz / mono / s16 = 640 bytes
    private const int RmsAnalysisFrameBytes = AudioSampleRate * AudioChannels * AudioBytesPerSample * 20 / 1000;
    private const string DefaultHttpUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";
    private static readonly string[] RequiredFfmpegLibraries = ["avutil", "avcodec", "avformat", "swresample"];

    private static int ffmpegInitialized;
    private int disposed;
    private readonly OperationsWorkerOptions options;
    private readonly ILogger<InProcessFfmpegAudioCapturePlugin> logger;
    private readonly IOperationalMetrics operationalMetrics;
    private readonly ChunkTranscriptionPipeline chunkTranscriptionPipeline;
    private readonly ConcurrentDictionary<string, CaptureSession> sessions = new(StringComparer.Ordinal);

    public InProcessFfmpegAudioCapturePlugin(
        OperationsWorkerOptions options,
        ILogger<InProcessFfmpegAudioCapturePlugin> logger,
        IOperationalMetrics operationalMetrics)
    {
        this.options = options;
        this.logger = logger;
        this.operationalMetrics = operationalMetrics;
        chunkTranscriptionPipeline = new ChunkTranscriptionPipeline(logger);
        EnsureFfmpegInitialized();
    }

    public async Task<AudioCaptureExecutionResult> CaptureAsync(
        CaptureSource source,
        PluginExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return new AudioCaptureExecutionResult(false, string.Empty, "Audio capture plugin is disposed.");
        }

        var mediaDirectory = BuildMediaDirectoryName(source.Media);

        // ConcurrentDictionary.AddOrUpdate evaluates both factory delegates before deciding
        // which one to use. That means CaptureSession.Start (which launches Task.Run internally)
        // could be called twice for the same sourceId under concurrent access, producing two
        // simultaneous capture sessions writing to different filenames for the same source.
        // Use GetOrAdd with a lazy sentinel to ensure Start is called at most once per sourceId.
        var session = sessions.GetOrAdd(source.SourceId, static (id, ctx) =>
            CaptureSession.Start(ctx.source, ctx.options.AudioOutputRootPath, ctx.mediaDirectory, ctx.plan, ctx.options, ctx.logger, ctx.operationalMetrics, ctx.chunkTranscriptionPipeline),
            (source, options, mediaDirectory, plan, logger, operationalMetrics, chunkTranscriptionPipeline));

        // If the existing session stopped (error or end-of-input), replace it atomically.
        if (!session.IsRunning && !session.CompletedByEndOfInput)
        {
            var replacement = CaptureSession.Start(source, options.AudioOutputRootPath, mediaDirectory, plan, options, logger, operationalMetrics, chunkTranscriptionPipeline);
            // Only replace if the stored value is still the stale one we just read.
            sessions.TryUpdate(source.SourceId, replacement, session);
            session = sessions[source.SourceId];
        }

        var startupResult = await session.WaitForStartupAsync(cancellationToken).ConfigureAwait(false);
        if (!startupResult.Succeeded)
        {
            return new AudioCaptureExecutionResult(false, startupResult.OpusFilePath, startupResult.ErrorMessage);
        }

        if (!session.IsRunning)
        {
            if (session.CompletedByEndOfInput && string.IsNullOrWhiteSpace(session.LastError))
            {
                return new AudioCaptureExecutionResult(true, session.LastOpusPath ?? session.CurrentOpusPath());
            }

            return new AudioCaptureExecutionResult(false, session.CurrentOpusPath(), session.LastError);
        }

        return new AudioCaptureExecutionResult(true, startupResult.OpusFilePath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }

        sessions.Clear();
        chunkTranscriptionPipeline.Dispose();
    }

    private static void EnsureFfmpegInitialized()
    {
        if (Interlocked.Exchange(ref ffmpegInitialized, 1) != 0)
        {
            return;
        }

        var rootPath = ResolveFfmpegRootPath();
        ffmpeg.RootPath = rootPath;

        try
        {
            FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

            // Force a representative libavformat symbol bind at startup to fail fast with context.
            _ = ffmpeg.avformat_version();
        }
        catch (Exception exception) when (exception is NotSupportedException or DllNotFoundException)
        {
            throw new InvalidOperationException(
                $"Unable to initialize FFmpeg shared libraries from '{rootPath}'. Ensure avutil/avcodec/avformat/swresample DLLs compatible with FFmpeg.AutoGen 8.1.0 are available.",
                exception);
        }
    }

    private static string ResolveFfmpegRootPath()
    {
        var embeddedDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        if (ContainsRequiredFfmpegLibraries(embeddedDirectory))
        {
            return embeddedDirectory;
        }

        throw new InvalidOperationException(
            $"No embedded FFmpeg shared libraries were found in '{embeddedDirectory}'. Include avutil/avcodec/avformat/swresample DLLs in the project under native/win-x64 so they are published into runtimes/win-x64/native.");
    }

    private static bool ContainsRequiredFfmpegLibraries(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var files = Directory.EnumerateFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToArray();

        return RequiredFfmpegLibraries.All(required =>
            files.Any(name => name!.StartsWith(required, StringComparison.OrdinalIgnoreCase)));
    }

    private static int ResolveConfiguredInt(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static bool ResolveBoolEnv(string key, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw is "1" or "true" or "yes";
    }

    private static DateTimeOffset AlignWindow(DateTimeOffset now, TimeSpan window)
    {
        var ticks = window.Ticks;
        var alignedTicks = (now.Ticks / ticks) * ticks;
        return new DateTimeOffset(alignedTicks, now.Offset);
    }

    private static string BuildMediaDirectoryName(string media)
    {
        if (string.IsNullOrWhiteSpace(media))
        {
            return "unknown";
        }

        var safeChars = media
            .Trim()
            .ToLowerInvariant()
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray();

        return safeChars.Length == 0 ? "unknown" : new string(safeChars);
    }

    private sealed unsafe class CaptureSession : IDisposable
    {
        private readonly TaskCompletionSource<AudioCaptureExecutionResult> startupCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task captureTask;
        private readonly string sourceId;
        private readonly string audioOutputRootPath;
        private readonly string mediaDirectory;
        private readonly PluginExecutionPlan plan;
        private readonly CaptureSource source;
        private readonly OperationsWorkerOptions options;
        private readonly ILogger logger;
        private readonly IOperationalMetrics operationalMetrics;
        private readonly ChunkTranscriptionPipeline chunkTranscriptionPipeline;
        private volatile string? activeOpusPath;
        private volatile bool completedByEndOfInput;
        private volatile bool isRunning;
        private volatile string? lastError;

        private CaptureSession(
            CaptureSource source,
            string audioOutputRootPath,
            string mediaDirectory,
            PluginExecutionPlan plan,
            OperationsWorkerOptions options,
            ILogger logger,
            IOperationalMetrics operationalMetrics,
            ChunkTranscriptionPipeline chunkTranscriptionPipeline)
        {
            this.source = source;
            this.audioOutputRootPath = audioOutputRootPath;
            this.mediaDirectory = mediaDirectory;
            this.plan = plan;
            this.options = options;
            this.logger = logger;
            this.operationalMetrics = operationalMetrics;
            this.chunkTranscriptionPipeline = chunkTranscriptionPipeline;
            sourceId = source.SourceId;
            isRunning = true;
            captureTask = Task.Run(RunAsync);
        }

        public static CaptureSession Start(
            CaptureSource source,
            string audioOutputRootPath,
            string mediaDirectory,
            PluginExecutionPlan plan,
            OperationsWorkerOptions options,
            ILogger logger,
            IOperationalMetrics operationalMetrics,
            ChunkTranscriptionPipeline chunkTranscriptionPipeline)
        {
            return new CaptureSession(source, audioOutputRootPath, mediaDirectory, plan, options, logger, operationalMetrics, chunkTranscriptionPipeline);
        }

        public bool IsRunning => isRunning && !captureTask.IsCompleted;

        public bool CompletedByEndOfInput => completedByEndOfInput;

        public string? LastError => lastError;

        public string? LastOpusPath => activeOpusPath;

        public string CurrentOpusPath() => CurrentOpusPath(SourceNow());

        public string CurrentOpusPath(DateTimeOffset now)
        {
            return CurrentOpusPath(now, plan.OpusRotationInterval);
        }

        public string CurrentOpusPath(DateTimeOffset now, TimeSpan rotationInterval)
        {
            var sourceDirectory = ResolveSourceDirectory(now);
            return Path.Combine(sourceDirectory, $"{sourceId}_{now:yyyy-MM-dd_HH-mm-ss}.opus");
        }

        public string CurrentTranscriptionJsonPath(string opusPath)
        {
            return Path.ChangeExtension(opusPath, ".json");
        }

        public static string StatsJsonPath(string transcriptionJsonPath)
        {
            var dir = Path.GetDirectoryName(transcriptionJsonPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(transcriptionJsonPath);
            return Path.Combine(dir, name + ".stats.json");
        }

        private string ResolveSourceDirectory(DateTimeOffset timestamp)
        {
            var sourceDirectory = Path.Combine(
                audioOutputRootPath,
                mediaDirectory,
                timestamp.ToString("yyyy"),
                timestamp.ToString("MM"),
                timestamp.ToString("dd"),
                sourceId);

            Directory.CreateDirectory(sourceDirectory);
            return sourceDirectory;
        }

        private DateTimeOffset SourceNow()
        {
            return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(source.UtcOffsetMinutes));
        }

        public Task<AudioCaptureExecutionResult> WaitForStartupAsync(CancellationToken cancellationToken)
        {
            return startupCompletionSource.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            try
            {
                cancellationTokenSource.Cancel();
                if (!captureTask.Wait(TimeSpan.FromSeconds(30)))
                {
                    logger.LogWarning("Capture session for source {SourceId} did not stop within the graceful timeout and may be interrupted abruptly.", sourceId);
                }
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Capture session dispose for source {SourceId} observed a non-fatal exception.", sourceId);
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }

        private void SetFailure(string message)
        {
            lastError = message;
            completedByEndOfInput = false;
            isRunning = false;
        }

        private Task RunAsync()
        {
            AVFormatContext* inputContext = null;
            AVCodecContext* decoderContext = null;
            SwrContext* swrContext = null;
            AVCodecContext* encoderContext = null;
            AVFormatContext* outputContext = null;
            AVStream* outputStream = null;
            AVFrame* inputFrame = null;
            AVFrame* resampledFrame = null;
            AVFrame* encoderFrame = null;
            AVPacket* inputPacket = null;
            AVPacket* outputPacket = null;
            AVDictionary* inputOptions = null;
            AVFormatContext* inputContextPtr = null;
            PcmByteQueue? pendingOpusPcm = null;
            PcmByteQueue? pendingFlacPcm = null;
            var encoderSampleCursor = 0L;
            var lastFlushAt = SourceNow();
            var effectiveOpusRotationInterval = plan.OpusRotationInterval > TimeSpan.Zero
                ? plan.OpusRotationInterval
                : TimeSpan.FromHours(1);
            var nextFlacWindowAt = AlignWindow(lastFlushAt, plan.FlacWindowDuration).Add(plan.FlacWindowDuration);
            var nextRotationAt = AlignWindow(lastFlushAt, effectiveOpusRotationInterval).Add(effectiveOpusRotationInterval);
            var encoderFrameSize = 0;
            string? currentTranscriptionJsonPath = null;
            var currentOpusStartedAt = SourceNow();
            var currentOpusSampleCursor = 0L;
            long? flacChunkStartSample = null;
            string? flacChunkTranscriptionJsonPath = null;
            byte[]? transcriptionOverlapTailPcm = null;
            ChunkingState? chunkingState = null;
            var consecutivePacketSendErrors = 0;
            var consecutiveEncoderFrameSendFailures = 0;
            const int maxConsecutivePacketSendErrors = 8;
            const int maxConsecutiveEncoderFrameSendFailures = 48;

            try
            {
                var isHttpStream = source.StreamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                   source.StreamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                var isRtspStream = source.StreamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

                // HTTP/HTTPS streams need gentler probing; RTSP/local can be aggressive
                ffmpeg.av_dict_set(&inputOptions, "probesize", isHttpStream ? "131072" : "32768", 0);
                ffmpeg.av_dict_set(&inputOptions, "analyzeduration", isHttpStream ? "1000000" : "0", 0);
                
                if (!isHttpStream)
                {
                    ffmpeg.av_dict_set(&inputOptions, "fflags", "nobuffer", 0);
                }

                ffmpeg.av_dict_set(&inputOptions, "stimeout", "5000000", 0);

                if (options.EnableDecoderReconnect)
                {
                    ffmpeg.av_dict_set(&inputOptions, "reconnect", "1", 0);
                    ffmpeg.av_dict_set(&inputOptions, "reconnect_streamed", "1", 0);
                    ffmpeg.av_dict_set(&inputOptions, "reconnect_delay_max", options.DecoderReconnectDelayMaxSeconds.ToString(), 0);
                }

                if (options.RtspPreferTcp && isRtspStream)
                {
                    ffmpeg.av_dict_set(&inputOptions, "rtsp_transport", "tcp", 0);
                }

                var openResult = ffmpeg.avformat_open_input(&inputContextPtr, source.StreamUrl, null, &inputOptions);
                if (openResult < 0 && isHttpStream)
                {
                    if (inputContextPtr is not null)
                    {
                        ffmpeg.avformat_close_input(&inputContextPtr);
                    }

                    ffmpeg.av_dict_free(&inputOptions);

                    // Rebuild baseline options and retry once with browser-like HTTP headers.
                    ffmpeg.av_dict_set(&inputOptions, "probesize", "131072", 0);
                    ffmpeg.av_dict_set(&inputOptions, "analyzeduration", "1000000", 0);
                    ffmpeg.av_dict_set(&inputOptions, "stimeout", "5000000", 0);

                    if (options.EnableDecoderReconnect)
                    {
                        ffmpeg.av_dict_set(&inputOptions, "reconnect", "1", 0);
                        ffmpeg.av_dict_set(&inputOptions, "reconnect_streamed", "1", 0);
                        ffmpeg.av_dict_set(&inputOptions, "reconnect_delay_max", options.DecoderReconnectDelayMaxSeconds.ToString(), 0);
                    }

                    ffmpeg.av_dict_set(&inputOptions, "user_agent", DefaultHttpUserAgent, 0);

                    var headers = BuildHttpRequestHeaders(source.StreamUrl);
                    if (!string.IsNullOrWhiteSpace(headers))
                    {
                        ffmpeg.av_dict_set(&inputOptions, "headers", headers, 0);
                    }

                    openResult = ffmpeg.avformat_open_input(&inputContextPtr, source.StreamUrl, null, &inputOptions);
                }

                openResult.ThrowIfError("avformat_open_input");
                inputContext = inputContextPtr;
                ffmpeg.avformat_find_stream_info(inputContext, null).ThrowIfError("avformat_find_stream_info");

                AVCodec* decoder = null;
                var audioStreamIndex = ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
                if (audioStreamIndex < 0 || decoder is null)
                {
                    throw new InvalidOperationException($"No audio stream found for source {source.SourceId}.");
                }

                decoderContext = ffmpeg.avcodec_alloc_context3(decoder);
                if (decoderContext is null)
                {
                    throw new InvalidOperationException("Unable to allocate decoder context.");
                }

                ffmpeg.avcodec_parameters_to_context(decoderContext, inputContext->streams[audioStreamIndex]->codecpar).ThrowIfError("avcodec_parameters_to_context");
                ffmpeg.avcodec_open2(decoderContext, decoder, null).ThrowIfError("avcodec_open2(decoder)");

                swrContext = ffmpeg.swr_alloc();
                if (swrContext is null)
                {
                    throw new InvalidOperationException("Unable to allocate resampler context.");
                }

                AVChannelLayout inputLayout = decoderContext->ch_layout;
                if (inputLayout.nb_channels <= 0)
                {
                    try
                    {
                        ffmpeg.av_channel_layout_default(&inputLayout, 1);
                    }
                    catch (NotSupportedException exception)
                    {
                        throw new InvalidOperationException("av_channel_layout_default(input) is not supported by the loaded FFmpeg bindings.", exception);
                    }
                }

                AVChannelLayout outputLayout = default;
                try
                {
                    ffmpeg.av_channel_layout_default(&outputLayout, AudioChannels);
                }
                catch (NotSupportedException exception)
                {
                    throw new InvalidOperationException("av_channel_layout_default(output) is not supported by the loaded FFmpeg bindings.", exception);
                }

                ffmpeg.av_opt_set_chlayout(swrContext, "in_chlayout", &inputLayout, 0).ThrowIfError("av_opt_set_chlayout(in)");
                ffmpeg.av_opt_set_chlayout(swrContext, "out_chlayout", &outputLayout, 0).ThrowIfError("av_opt_set_chlayout(out)");
                ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", decoderContext->sample_rate, 0).ThrowIfError("av_opt_set_int(in_sample_rate)");
                ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", AudioSampleRate, 0).ThrowIfError("av_opt_set_int(out_sample_rate)");
                ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", decoderContext->sample_fmt, 0).ThrowIfError("av_opt_set_sample_fmt(in_sample_fmt)");
                ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0).ThrowIfError("av_opt_set_sample_fmt(out_sample_fmt)");
                ffmpeg.swr_init(swrContext).ThrowIfError("swr_init");

                AVCodec* encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_OPUS);
                if (encoder is null)
                {
                    throw new InvalidOperationException("OPUS encoder not found.");
                }

                encoderContext = ffmpeg.avcodec_alloc_context3(encoder);
                if (encoderContext is null)
                {
                    throw new InvalidOperationException("Unable to allocate OPUS encoder context.");
                }

                encoderContext->sample_rate = AudioSampleRate;
                try
                {
                    ffmpeg.av_channel_layout_default(&encoderContext->ch_layout, AudioChannels);
                }
                catch (NotSupportedException exception)
                {
                    throw new InvalidOperationException("av_channel_layout_default(encoder) is not supported by the loaded FFmpeg bindings.", exception);
                }
                encoderContext->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                encoderContext->time_base = new AVRational { num = 1, den = AudioSampleRate };
                encoderContext->bit_rate = Math.Max(6, options.DefaultOpusBitrateKbps) * 1000L;
                if (encoderContext->priv_data is not null)
                {
                    ffmpeg.av_opt_set(encoderContext->priv_data, "compression_level", "0", 0);
                }

                ffmpeg.avcodec_open2(encoderContext, encoder, null).ThrowIfError("avcodec_open2(encoder)");
                encoderFrameSize = encoderContext->frame_size > 0 ? encoderContext->frame_size : AudioSampleRate / 2;
                pendingOpusPcm = new PcmByteQueue();
                pendingFlacPcm = new PcmByteQueue();
                chunkingState = options.EnableFlacSilenceChunking
                    ? ChunkingState.Create(options, logger, sourceId)
                    : null;

                activeOpusPath = CurrentOpusPath(SourceNow(), effectiveOpusRotationInterval);
                currentTranscriptionJsonPath = CurrentTranscriptionJsonPath(activeOpusPath);
                currentOpusStartedAt = SourceNow();
                currentOpusSampleCursor = 0;
                outputContext = OpenOutputContext(activeOpusPath, encoderContext, ref outputStream);
                startupCompletionSource.TrySetResult(new AudioCaptureExecutionResult(true, activeOpusPath));
                logger.LogInformation("Capture started for source {SourceId}. Reconnect={ReconnectEnabled}, RtspTcp={RtspPreferTcp}, SilenceChunking={SilenceChunkingEnabled}, OpusBitrateKbps={OpusBitrateKbps}.", sourceId, options.EnableDecoderReconnect, options.RtspPreferTcp, chunkingState is not null, options.DefaultOpusBitrateKbps);
                logger.LogInformation("OPUS rotation interval for source {SourceId}: profile={ProfileRotationMinutes} min, effective={EffectiveRotationMinutes} min.", sourceId, plan.OpusRotationInterval.TotalMinutes, effectiveOpusRotationInterval.TotalMinutes);

                inputPacket = ffmpeg.av_packet_alloc();
                inputFrame = ffmpeg.av_frame_alloc();
                resampledFrame = ffmpeg.av_frame_alloc();
                encoderFrame = ffmpeg.av_frame_alloc();
                outputPacket = ffmpeg.av_packet_alloc();

                if (inputPacket is null || inputFrame is null || resampledFrame is null || encoderFrame is null || outputPacket is null)
                {
                    throw new InvalidOperationException("Unable to allocate FFmpeg packets or frames.");
                }

                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    ffmpeg.av_packet_unref(inputPacket);
                    var readResult = ffmpeg.av_read_frame(inputContext, inputPacket);
                    if (readResult == ffmpeg.AVERROR_EOF)
                    {
                        completedByEndOfInput = true;
                        break;
                    }

                    readResult.ThrowIfError("av_read_frame");

                    if (inputPacket->stream_index != audioStreamIndex)
                    {
                        continue;
                    }

                    var sendPacketResult = ffmpeg.avcodec_send_packet(decoderContext, inputPacket);
                    if (sendPacketResult < 0)
                    {
                        consecutivePacketSendErrors++;

                        // Some HTTP/ICY streams emit malformed packets intermittently.
                        // Skip a bounded number of consecutive decoder send failures before aborting the session.
                        if (consecutivePacketSendErrors <= maxConsecutivePacketSendErrors)
                        {
                            if (consecutivePacketSendErrors == 1 || consecutivePacketSendErrors == maxConsecutivePacketSendErrors)
                            {
                                logger.LogWarning(
                                    "Transient decoder packet error for source {SourceId}. FFmpegCode={ErrorCode}, ConsecutiveErrors={ConsecutiveErrors}/{MaxConsecutiveErrors}.",
                                    sourceId,
                                    sendPacketResult,
                                    consecutivePacketSendErrors,
                                    maxConsecutivePacketSendErrors);
                            }

                            continue;
                        }

                        sendPacketResult.ThrowIfError("avcodec_send_packet");
                    }

                    consecutivePacketSendErrors = 0;

                    while (true)
                    {
                        var decodeResult = ffmpeg.avcodec_receive_frame(decoderContext, inputFrame);
                        if (decodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || decodeResult == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        decodeResult.ThrowIfError("avcodec_receive_frame");

                        var outSamples = checked((int)ffmpeg.av_rescale_rnd(
                            ffmpeg.swr_get_delay(swrContext, decoderContext->sample_rate) + inputFrame->nb_samples,
                            AudioSampleRate,
                            decoderContext->sample_rate,
                            AVRounding.AV_ROUND_UP));

                        ffmpeg.av_frame_unref(resampledFrame);
                        resampledFrame->nb_samples = outSamples;
                        resampledFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                        resampledFrame->sample_rate = AudioSampleRate;
                        try
                        {
                            ffmpeg.av_channel_layout_default(&resampledFrame->ch_layout, AudioChannels);
                        }
                        catch (NotSupportedException exception)
                        {
                            throw new InvalidOperationException("av_channel_layout_default(frame) is not supported by the loaded FFmpeg bindings.", exception);
                        }
                        ffmpeg.av_frame_get_buffer(resampledFrame, 0).ThrowIfError("av_frame_get_buffer");

                        ffmpeg.swr_convert_frame(swrContext, resampledFrame, inputFrame).ThrowIfError("swr_convert_frame");

                        var now = SourceNow();
                        var frameSampleCount = resampledFrame->nb_samples;

                        AppendSamples(pendingOpusPcm!, resampledFrame, AudioChannels);
                        EncodeBufferedSamples(
                            pendingOpusPcm!,
                            encoderFrameSize,
                            encoderContext,
                            encoderFrame,
                            outputContext,
                            outputStream,
                            outputPacket,
                            ref encoderSampleCursor,
                            ref consecutiveEncoderFrameSendFailures,
                            maxConsecutiveEncoderFrameSendFailures);

                        var flacChunkWasEmpty = pendingFlacPcm!.Length == 0;
                        AppendSamples(pendingFlacPcm!, resampledFrame, AudioChannels);
                        if (flacChunkWasEmpty)
                        {
                            flacChunkStartSample = currentOpusSampleCursor;
                            flacChunkTranscriptionJsonPath = currentTranscriptionJsonPath;
                        }

                        currentOpusSampleCursor += frameSampleCount;
                        var currentAudioTimeline = ResolveChunkTime(currentOpusStartedAt, currentOpusSampleCursor);

                        if (chunkingState is not null)
                        {
                            var cutDecision = chunkingState.Observe(resampledFrame);
                            if (cutDecision.ShouldCut)
                            {
                                var chunkStartSample = flacChunkStartSample ?? currentOpusSampleCursor;
                                var chunkStartedAt = ResolveChunkTime(currentOpusStartedAt, chunkStartSample);
                                var chunkEndedAt = ResolveChunkTime(currentOpusStartedAt, currentOpusSampleCursor);
                                var chunkTranscriptionJsonPath = flacChunkTranscriptionJsonPath ?? currentTranscriptionJsonPath;
                                EnqueueChunkTranscription(pendingFlacPcm!, chunkStartedAt, chunkEndedAt, chunkTranscriptionJsonPath, ref transcriptionOverlapTailPcm, preserveOverlapForNextChunk: true);
                                flacChunkStartSample = null;
                                flacChunkTranscriptionJsonPath = null;
                                logger.LogDebug("FLAC chunk cut for source {SourceId}. ForcedByMaxWindow={ForcedByMaxWindow}, ChunkSamples={ChunkSamples}.", sourceId, cutDecision.ForcedByMaxWindow, cutDecision.ChunkSamples);
                                chunkingState.ResetAfterFlush();
                            }
                        }
                        else if (now >= nextFlacWindowAt)
                        {
                            var chunkStartSample = flacChunkStartSample ?? currentOpusSampleCursor;
                            var chunkStartedAt = ResolveChunkTime(currentOpusStartedAt, chunkStartSample);
                            var chunkEndedAt = ResolveChunkTime(currentOpusStartedAt, currentOpusSampleCursor);
                            var chunkTranscriptionJsonPath = flacChunkTranscriptionJsonPath ?? currentTranscriptionJsonPath;
                            EnqueueChunkTranscription(pendingFlacPcm!, chunkStartedAt, chunkEndedAt, chunkTranscriptionJsonPath, ref transcriptionOverlapTailPcm, preserveOverlapForNextChunk: true);
                            flacChunkStartSample = null;
                            flacChunkTranscriptionJsonPath = null;
                            nextFlacWindowAt = AlignWindow(now, plan.FlacWindowDuration).Add(plan.FlacWindowDuration);
                        }

                        ffmpeg.av_frame_unref(inputFrame);
                        if (now - lastFlushAt >= plan.OpusFlushInterval)
                        {
                            if (outputContext->pb is not null)
                            {
                                ffmpeg.avio_flush(outputContext->pb);
                            }

                            lastFlushAt = now;
                        }

                        if (currentAudioTimeline >= nextRotationAt)
                        {
                            if (pendingFlacPcm!.Length > 0)
                            {
                                var chunkStartSample = flacChunkStartSample ?? currentOpusSampleCursor;
                                var chunkStartedAt = ResolveChunkTime(currentOpusStartedAt, chunkStartSample);
                                var chunkEndedAt = ResolveChunkTime(currentOpusStartedAt, currentOpusSampleCursor);
                                var chunkTranscriptionJsonPath = flacChunkTranscriptionJsonPath ?? currentTranscriptionJsonPath;
                                EnqueueChunkTranscription(pendingFlacPcm, chunkStartedAt, chunkEndedAt, chunkTranscriptionJsonPath, ref transcriptionOverlapTailPcm, preserveOverlapForNextChunk: false);
                                flacChunkStartSample = null;
                                flacChunkTranscriptionJsonPath = null;
                                chunkingState?.ResetAfterFlush();
                            }

                            RotateOutput(ref outputContext, ref outputStream, encoderContext, outputPacket, nextRotationAt, effectiveOpusRotationInterval);
                            currentTranscriptionJsonPath = CurrentTranscriptionJsonPath(activeOpusPath);
                            currentOpusStartedAt = nextRotationAt;
                            currentOpusSampleCursor = 0;
                            nextRotationAt = AlignWindow(currentOpusStartedAt, effectiveOpusRotationInterval).Add(effectiveOpusRotationInterval);
                        }
                    }
                }

                if (pendingOpusPcm is not null && pendingOpusPcm.Length > 0)
                {
                    EncodeBufferedSamples(
                        pendingOpusPcm,
                        int.MaxValue,
                        encoderContext,
                        encoderFrame!,
                        outputContext,
                        outputStream,
                        outputPacket,
                        ref encoderSampleCursor,
                        ref consecutiveEncoderFrameSendFailures,
                        maxConsecutiveEncoderFrameSendFailures,
                        flushFinal: true);
                }

                DrainEncoder(encoderContext, outputContext, outputStream, outputPacket);

                if (pendingFlacPcm is not null && pendingFlacPcm.Length > 0)
                {
                    var chunkStartSample = flacChunkStartSample ?? currentOpusSampleCursor;
                    var chunkStartedAt = ResolveChunkTime(currentOpusStartedAt, chunkStartSample);
                    var chunkEndedAt = ResolveChunkTime(currentOpusStartedAt, currentOpusSampleCursor);
                    var chunkTranscriptionJsonPath = flacChunkTranscriptionJsonPath ?? currentTranscriptionJsonPath;
                    EnqueueChunkTranscription(pendingFlacPcm, chunkStartedAt, chunkEndedAt, chunkTranscriptionJsonPath, ref transcriptionOverlapTailPcm, preserveOverlapForNextChunk: false);
                }
                isRunning = false;
                logger.LogInformation("Capture completed for source {SourceId}.", sourceId);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                var detailedError = exception.ToString();
                SetFailure(detailedError);
                operationalMetrics.RecordCaptureRuntimeFailure(sourceId);
                logger.LogError(exception, "Capture failed for source {SourceId}.", sourceId);
                startupCompletionSource.TrySetResult(new AudioCaptureExecutionResult(false, activeOpusPath ?? CurrentOpusPath(), detailedError));
                return Task.CompletedTask;
            }
            finally
            {
                isRunning = false;

                pendingOpusPcm = null;
                pendingFlacPcm = null;

                if (outputContext is not null)
                {
                    try
                    {
                        ffmpeg.av_write_trailer(outputContext);
                    }
                    catch
                    {
                    }

                    if (outputContext->pb is not null)
                    {
                        ffmpeg.avio_closep(&outputContext->pb);
                    }

                    ffmpeg.avformat_free_context(outputContext);
                }

                if (outputPacket is not null)
                {
                    ffmpeg.av_packet_free(&outputPacket);
                }

                if (resampledFrame is not null)
                {
                    ffmpeg.av_frame_free(&resampledFrame);
                }

                if (encoderFrame is not null)
                {
                    ffmpeg.av_frame_free(&encoderFrame);
                }

                if (inputFrame is not null)
                {
                    ffmpeg.av_frame_free(&inputFrame);
                }

                if (inputPacket is not null)
                {
                    ffmpeg.av_packet_free(&inputPacket);
                }

                if (encoderContext is not null)
                {
                    AVCodecContext* encoderToFree = encoderContext;
                    ffmpeg.avcodec_free_context(&encoderToFree);
                }

                if (swrContext is not null)
                {
                    ffmpeg.swr_free(&swrContext);
                }

                if (decoderContext is not null)
                {
                    AVCodecContext* decoderToFree = decoderContext;
                    ffmpeg.avcodec_free_context(&decoderToFree);
                }

                if (inputContext is not null)
                {
                    ffmpeg.avformat_close_input(&inputContext);
                }

                if (inputOptions is not null)
                {
                    ffmpeg.av_dict_free(&inputOptions);
                }
            }
        }

        private AVFormatContext* OpenOutputContext(string outputPath, AVCodecContext* encoderContext, ref AVStream* outputStream)
        {
            AVFormatContext* context = null;
            ffmpeg.avformat_alloc_output_context2(&context, null, "opus", outputPath).ThrowIfError("avformat_alloc_output_context2");

            outputStream = ffmpeg.avformat_new_stream(context, null);
            if (outputStream is null)
            {
                throw new InvalidOperationException("Unable to create output stream.");
            }

            ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoderContext).ThrowIfError("avcodec_parameters_from_context");
            outputStream->time_base = encoderContext->time_base;
            ffmpeg.avio_open(&context->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open");
            ffmpeg.avformat_write_header(context, null).ThrowIfError("avformat_write_header");
            return context;
        }

        private static string BuildHttpRequestHeaders(string streamUrl)
        {
            if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            var origin = uri.IsDefaultPort
                ? $"{uri.Scheme}://{uri.Host}"
                : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            var referer = $"{origin}/";

            return $"Referer: {referer}\r\nOrigin: {origin}\r\nAccept: */*\r\n";
        }

        private void RotateOutput(ref AVFormatContext* outputContext, ref AVStream* outputStream, AVCodecContext* encoderContext, AVPacket* packet, DateTimeOffset now, TimeSpan rotationInterval)
        {
            if (outputContext is null)
            {
                return;
            }

            if (outputContext->pb is not null)
            {
                ffmpeg.avio_flush(outputContext->pb);
            }

            // Drain pending frames from encoder to current output file before closing
            DrainEncoder(encoderContext, outputContext, outputStream, packet);

            ffmpeg.av_write_trailer(outputContext);

            if (outputContext->pb is not null)
            {
                ffmpeg.avio_closep(&outputContext->pb);
            }

            ffmpeg.avformat_free_context(outputContext);
            activeOpusPath = CurrentOpusPath(now, rotationInterval);
            outputContext = OpenOutputContext(activeOpusPath, encoderContext, ref outputStream);
        }

        private void DrainEncoder(AVCodecContext* encoderContext, AVFormatContext* outputContext, AVStream* outputStream, AVPacket* packet)
        {
            if (!TrySendFrameWithRecovery(encoderContext, null, outputContext, outputStream, packet, 0, flushFinal: true))
            {
                logger.LogWarning("Skipping encoder flush for source {SourceId} after repeated send_frame failure.", sourceId);
                return;
            }

            while (true)
            {
                ffmpeg.av_packet_unref(packet);
                var result = ffmpeg.avcodec_receive_packet(encoderContext, packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                result.ThrowIfError("avcodec_receive_packet(flush)");
                packet->stream_index = outputStream->index;
                ffmpeg.av_packet_rescale_ts(packet, encoderContext->time_base, outputStream->time_base);
                ffmpeg.av_interleaved_write_frame(outputContext, packet).ThrowIfError("av_interleaved_write_frame(flush)");
            }
        }

        private static void AppendSamples(PcmByteQueue pendingPcm, AVFrame* frame, int channels)
        {
            var pcmBytes = checked((int)(frame->nb_samples * channels * AudioBytesPerSample));
            pendingPcm.AppendFromFrame(frame, pcmBytes);
        }

        private void EnqueueChunkTranscription(
            PcmByteQueue pcmBuffer,
            DateTimeOffset chunkStartedAt,
            DateTimeOffset chunkEndedAt,
            string? transcriptionJsonPath,
            ref byte[]? overlapTailPcm,
            bool preserveOverlapForNextChunk)
        {
            if (pcmBuffer.Length == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(transcriptionJsonPath))
            {
                pcmBuffer.ResetAfterWrite();
                overlapTailPcm = null;
                return;
            }

            var currentPcm = pcmBuffer.Snapshot();
            if (currentPcm.Length == 0)
            {
                return;
            }

            var payloadPcm = ConcatPcm(overlapTailPcm, currentPcm);
            var preprocessedPcm = PreprocessPcmForRecognition(payloadPcm);
            var flacWindows = BuildFlacRecognitionWindows(preprocessedPcm);
            if (flacWindows.Count == 0)
            {
                pcmBuffer.ResetAfterWrite();
                return;
            }

            chunkTranscriptionPipeline.Enqueue(new ChunkTranscriptionRequest(
                transcriptionJsonPath,
                CaptureSession.StatsJsonPath(transcriptionJsonPath),
                chunkStartedAt,
                chunkEndedAt,
                flacWindows,
                sourceId,
                ForceFlush: !preserveOverlapForNextChunk));

            if (preserveOverlapForNextChunk)
            {
                var overlapBytes = TranscriptionChunkOverlapSeconds * AudioSampleRate * AudioChannels * AudioBytesPerSample;
                overlapTailPcm = TailPcm(currentPcm, overlapBytes);
            }
            else
            {
                overlapTailPcm = null;
            }

            pcmBuffer.ResetAfterWrite();
        }

        private static DateTimeOffset ResolveChunkTime(DateTimeOffset opusStartedAt, long sampleOffset)
        {
            if (sampleOffset <= 0)
            {
                return opusStartedAt;
            }

            var ticks = (sampleOffset * TimeSpan.TicksPerSecond) / AudioSampleRate;
            return opusStartedAt.AddTicks(ticks);
        }

        private static byte[] EncodeFlacChunkBytes(byte[] pcmSnapshot)
        {
            if (pcmSnapshot.Length == 0)
            {
                return [];
            }

            AVFormatContext* outputContext = null;
            AVCodecContext* encoderContext = null;
            AVStream* outputStream = null;
            AVFrame* frame = null;
            AVPacket* packet = null;
            byte* dynamicBuffer = null;
            byte[]? encodedBytes = null;

            try
            {
                AVCodec* encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC);
                if (encoder is null)
                {
                    throw new InvalidOperationException("FLAC encoder not found.");
                }

                ffmpeg.avformat_alloc_output_context2(&outputContext, null, "flac", null).ThrowIfError("avformat_alloc_output_context2(flac)");

                encoderContext = ffmpeg.avcodec_alloc_context3(encoder);
                if (encoderContext is null)
                {
                    throw new InvalidOperationException("Unable to allocate FLAC encoder context.");
                }

                encoderContext->sample_rate = AudioSampleRate;
                encoderContext->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                encoderContext->time_base = new AVRational { num = 1, den = AudioSampleRate };

                try
                {
                    ffmpeg.av_channel_layout_default(&encoderContext->ch_layout, AudioChannels);
                }
                catch (NotSupportedException exception)
                {
                    throw new InvalidOperationException("av_channel_layout_default(flac_encoder) is not supported by the loaded FFmpeg bindings.", exception);
                }

                if ((outputContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                {
                    encoderContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                }

                ffmpeg.avcodec_open2(encoderContext, encoder, null).ThrowIfError("avcodec_open2(flac_encoder)");

                outputStream = ffmpeg.avformat_new_stream(outputContext, null);
                if (outputStream is null)
                {
                    throw new InvalidOperationException("Unable to create FLAC output stream.");
                }

                ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoderContext).ThrowIfError("avcodec_parameters_from_context(flac)");
                outputStream->time_base = encoderContext->time_base;

                ffmpeg.avio_open_dyn_buf(&outputContext->pb).ThrowIfError("avio_open_dyn_buf(flac)");
                outputContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
                ffmpeg.avformat_write_header(outputContext, null).ThrowIfError("avformat_write_header(flac)");

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();
                if (frame is null || packet is null)
                {
                    throw new InvalidOperationException("Unable to allocate FLAC frame or packet.");
                }

                var frameSamples = encoderContext->frame_size > 0 ? encoderContext->frame_size : 4096;
                var bytesPerSample = AudioChannels * AudioBytesPerSample;
                var offset = 0;
                var pts = 0L;

                while (offset < pcmSnapshot.Length)
                {
                    var availableSamples = (pcmSnapshot.Length - offset) / bytesPerSample;
                    if (availableSamples <= 0)
                    {
                        break;
                    }

                    var samplesToEncode = Math.Min(frameSamples, availableSamples);
                    var bytesToEncode = samplesToEncode * bytesPerSample;

                    ffmpeg.av_frame_unref(frame);
                    frame->nb_samples = samplesToEncode;
                    frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                    frame->sample_rate = AudioSampleRate;
                    frame->pts = pts;
                    pts += samplesToEncode;

                    try
                    {
                        ffmpeg.av_channel_layout_default(&frame->ch_layout, AudioChannels);
                    }
                    catch (NotSupportedException exception)
                    {
                        throw new InvalidOperationException("av_channel_layout_default(flac_frame) is not supported by the loaded FFmpeg bindings.", exception);
                    }

                    ffmpeg.av_frame_get_buffer(frame, 0).ThrowIfError("av_frame_get_buffer(flac)");
                    Marshal.Copy(pcmSnapshot, offset, (IntPtr)frame->data[0], bytesToEncode);
                    offset += bytesToEncode;

                    ffmpeg.avcodec_send_frame(encoderContext, frame).ThrowIfError("avcodec_send_frame(flac)");
                    WriteAvailableFlacPackets(outputContext, outputStream, encoderContext, packet);
                }

                ffmpeg.avcodec_send_frame(encoderContext, null).ThrowIfError("avcodec_send_frame(flac_flush)");
                WriteAvailableFlacPackets(outputContext, outputStream, encoderContext, packet);

                ffmpeg.av_write_trailer(outputContext).ThrowIfError("av_write_trailer(flac)");

                var dynamicSize = ffmpeg.avio_close_dyn_buf(outputContext->pb, &dynamicBuffer);
                outputContext->pb = null;
                if (dynamicSize < 0)
                {
                    throw new InvalidOperationException($"avio_close_dyn_buf(flac) failed with FFmpeg error code {dynamicSize}.");
                }

                encodedBytes = new byte[dynamicSize];
                if (dynamicSize > 0)
                {
                    Marshal.Copy((IntPtr)dynamicBuffer, encodedBytes, 0, dynamicSize);
                }

            }
            finally
            {
                if (dynamicBuffer is not null)
                {
                    ffmpeg.av_free(dynamicBuffer);
                }

                if (packet is not null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                if (frame is not null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (outputContext is not null)
                {
                    if (outputContext->pb is not null)
                    {
                        ffmpeg.avio_context_free(&outputContext->pb);
                    }

                    ffmpeg.avformat_free_context(outputContext);
                }

                if (encoderContext is not null)
                {
                    AVCodecContext* encoderToFree = encoderContext;
                    ffmpeg.avcodec_free_context(&encoderToFree);
                }
            }

            return encodedBytes ?? [];
        }

        private static byte[] ConcatPcm(byte[]? prefix, byte[] current)
        {
            if (prefix is null || prefix.Length == 0)
            {
                return current;
            }

            var combined = new byte[prefix.Length + current.Length];
            Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
            Buffer.BlockCopy(current, 0, combined, prefix.Length, current.Length);
            return combined;
        }

        private static byte[] TailPcm(byte[] pcm, int tailBytes)
        {
            if (pcm.Length == 0 || tailBytes <= 0)
            {
                return [];
            }

            if (pcm.Length <= tailBytes)
            {
                var clone = new byte[pcm.Length];
                Buffer.BlockCopy(pcm, 0, clone, 0, pcm.Length);
                return clone;
            }

            var tail = new byte[tailBytes];
            Buffer.BlockCopy(pcm, pcm.Length - tailBytes, tail, 0, tailBytes);
            return tail;
        }

        private static byte[] PreprocessPcmForRecognition(byte[] pcm)
        {
            if (pcm.Length < AudioBytesPerSample)
            {
                return pcm;
            }

            var sampleCount = pcm.Length / AudioBytesPerSample;
            if (sampleCount <= 0)
            {
                return pcm;
            }

            var filtered = ArrayPool<float>.Shared.Rent(sampleCount);
            try
            {
            var bytes = pcm.AsSpan();

            // First-order high-pass filter to reduce low-frequency hum/rumble.
            const double highPassCutoffHz = 120d;
            var dt = 1d / AudioSampleRate;
            var rc = 1d / (2d * Math.PI * highPassCutoffHz);
            var alpha = rc / (rc + dt);

            var previousInput = 0d;
            var previousOutput = 0d;
            var squareSum = 0d;

            for (var i = 0; i < sampleCount; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * AudioBytesPerSample, AudioBytesPerSample));
                var normalized = sample / 32768d;
                var hp = alpha * (previousOutput + normalized - previousInput);
                previousInput = normalized;
                previousOutput = hp;

                filtered[i] = (float)hp;
                squareSum += hp * hp;
            }

            var rms = Math.Sqrt(squareSum / sampleCount);
            const double targetRms = 0.18d;
            const double minGain = 0.5d;
            const double maxGain = 4.0d;
            var gain = rms > 1e-7 ? Math.Clamp(targetRms / rms, minGain, maxGain) : 1d;

            var output = new byte[sampleCount * AudioBytesPerSample];
            var outputSpan = output.AsSpan();

            for (var i = 0; i < sampleCount; i++)
            {
                var amplified = filtered[i] * gain;
                var clamped = Math.Clamp(amplified, -0.9999695d, 0.9999695d);
                var quantized = (short)Math.Round(clamped * 32768d);
                BinaryPrimitives.WriteInt16LittleEndian(outputSpan.Slice(i * AudioBytesPerSample, AudioBytesPerSample), quantized);
            }

            return output;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(filtered);
            }
        }

        private static IReadOnlyList<byte[]> BuildFlacRecognitionWindows(byte[] pcm)
        {
            if (pcm.Length == 0)
            {
                return [];
            }

            var bytesPerSecond = AudioSampleRate * AudioChannels * AudioBytesPerSample;
            var windowBytes = Math.Max(bytesPerSecond, RecognitionWindowSeconds * bytesPerSecond);
            var overlapBytes = Math.Clamp(RecognitionWindowOverlapSeconds * bytesPerSecond, 0, windowBytes / 2);
            var stepBytes = Math.Max(1, windowBytes - overlapBytes);
            var searchBytes = RecognitionVadSearchSeconds * bytesPerSecond;

            // Compute the dynamic silence floor once for the whole PCM buffer.
            // This replaces a fixed -40 dB threshold with one derived from the actual
            // audio content: quietest 5th-percentile frame * multiplier.
            var silenceFloor = searchBytes > 0
                ? ComputeDynamicSilenceFloor(pcm)
                : 0.0;

            // Build VAD-aligned cut points: instead of cutting at exactly stepBytes,
            // search for the quietest / first-silence frame within ±searchBytes of the
            // target position.  If no silence is found, the original target is kept.
            var cuts = new List<int>();
            var pos = 0;
            while (pos < pcm.Length)
            {
                cuts.Add(pos);
                var nextTarget = pos + stepBytes;
                if (nextTarget >= pcm.Length)
                {
                    break;
                }

                var nextCut = searchBytes > 0
                    ? FindSilenceCutPoint(pcm, nextTarget, searchBytes, silenceFloor, bytesPerSecond)
                    : nextTarget;

                // Clamp to at least 1s advance to avoid infinite loops on flat audio.
                pos = Math.Max(nextCut, pos + bytesPerSecond);
            }

            // Encode each window [cuts[i] … min(cuts[i+1] + overlap, end)].
            var windows = new List<byte[]>(cuts.Count);
            for (var i = 0; i < cuts.Count; i++)
            {
                var start = cuts[i];
                var end = i + 1 < cuts.Count
                    ? Math.Min(cuts[i + 1] + overlapBytes, pcm.Length)
                    : pcm.Length;

                if (start >= end)
                {
                    break;
                }

                var windowPcm = new byte[end - start];
                Buffer.BlockCopy(pcm, start, windowPcm, 0, end - start);
                var flac = EncodeFlacChunkBytes(windowPcm);
                if (flac.Length > 0)
                {
                    windows.Add(flac);
                }
            }

            return windows;
        }

        // Returns the dynamic silence threshold for this PCM buffer.
        // Computes RMS for every 20 ms frame, sorts them, and uses the 5th-percentile
        // value (the quietest ~5 % of frames) multiplied by a factor as the floor.
        // This avoids hard-coded dB values and self-calibrates to the actual noise level.
        private static double ComputeDynamicSilenceFloor(byte[] pcm, double floorMultiplier = 2.5)
        {
            var frameCount = pcm.Length / RmsAnalysisFrameBytes;
            if (frameCount < 4)
            {
                return 0.01;
            }

            var rmsValues = ArrayPool<double>.Shared.Rent(frameCount);
            try
            {
                for (var i = 0; i < frameCount; i++)
                {
                    rmsValues[i] = ComputeFrameRms(pcm, i * RmsAnalysisFrameBytes, RmsAnalysisFrameBytes);
                }

                Array.Sort(rmsValues, 0, frameCount);
                var p5Index = Math.Max(0, (int)(frameCount * 0.05) - 1);
                return Math.Max(rmsValues[p5Index] * floorMultiplier, 0.005);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(rmsValues);
            }
        }

        // Searches within [target − searchBytes, target + searchBytes/2] for a 20 ms
        // frame whose RMS is at or below silenceFloor (= true silence).  If none is
        // found, returns the frame with the lowest RMS (= nearest-silence fallback).
        // The returned offset is always at least minOffset to keep windows non-trivial.
        private static int FindSilenceCutPoint(byte[] pcm, int target, int searchBytes, double silenceFloor, int bytesPerSecond)
        {
            var searchStart = Math.Max(target - searchBytes, bytesPerSecond);
            var searchEnd = Math.Min(target + searchBytes / 2, pcm.Length - RmsAnalysisFrameBytes);

            if (searchStart >= searchEnd)
            {
                return Math.Clamp(target, 0, pcm.Length);
            }

            var bestSilenceOffset = -1;
            var bestSilenceDistance = int.MaxValue;
            var bestFallbackOffset = target;
            var bestFallbackRms = double.MaxValue;

            for (var offset = searchStart; offset <= searchEnd; offset += RmsAnalysisFrameBytes)
            {
                var rms = ComputeFrameRms(pcm, offset, Math.Min(RmsAnalysisFrameBytes, pcm.Length - offset));
                var dist = Math.Abs(offset - target);

                if (rms <= silenceFloor)
                {
                    // Prefer the silence frame closest to the original target.
                    if (dist < bestSilenceDistance)
                    {
                        bestSilenceDistance = dist;
                        bestSilenceOffset = offset;
                    }
                }
                else if (bestSilenceOffset < 0 && rms < bestFallbackRms)
                {
                    bestFallbackRms = rms;
                    bestFallbackOffset = offset;
                }
            }

            return Math.Min(bestSilenceOffset >= 0 ? bestSilenceOffset : bestFallbackOffset, pcm.Length);
        }

        // Computes the RMS of a single PCM frame (s16 little-endian).
        private static double ComputeFrameRms(byte[] pcm, int offset, int length)
        {
            var samples = length / AudioBytesPerSample;
            if (samples == 0)
            {
                return 0.0;
            }

            var sum = 0.0;
            var span = pcm.AsSpan(offset, length);
            for (var i = 0; i < samples; i++)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * AudioBytesPerSample, AudioBytesPerSample));
                var n = s / 32768.0;
                sum += n * n;
            }

            return Math.Sqrt(sum / samples);
        }

        private static List<(int Offset, int Length)> BuildWindowOffsets(int totalBytes, int startOffset, int stepBytes, int windowBytes)
        {
            var result = new List<(int, int)>();
            for (var offset = startOffset; offset < totalBytes; offset += stepBytes)
            {
                var remaining = totalBytes - offset;
                if (remaining <= 0)
                {
                    break;
                }

                var length = Math.Min(windowBytes, remaining);
                result.Add((offset, length));

                if (offset + length >= totalBytes)
                {
                    break;
                }
            }

            return result;
        }

        private static void WriteAvailableFlacPackets(AVFormatContext* outputContext, AVStream* outputStream, AVCodecContext* encoderContext, AVPacket* packet)
        {
            while (true)
            {
                ffmpeg.av_packet_unref(packet);
                var result = ffmpeg.avcodec_receive_packet(encoderContext, packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                result.ThrowIfError("avcodec_receive_packet(flac)");
                packet->stream_index = outputStream->index;
                ffmpeg.av_packet_rescale_ts(packet, encoderContext->time_base, outputStream->time_base);
                ffmpeg.av_interleaved_write_frame(outputContext, packet).ThrowIfError("av_interleaved_write_frame(flac)");
            }
        }

        private void EncodeBufferedSamples(
            PcmByteQueue pendingPcm,
            int frameSize,
            AVCodecContext* encoderContext,
            AVFrame* encoderFrame,
            AVFormatContext* outputContext,
            AVStream* outputStream,
            AVPacket* packet,
            ref long encoderSampleCursor,
            ref int consecutiveEncoderFrameSendFailures,
            int maxConsecutiveEncoderFrameSendFailures,
            bool flushFinal = false)
        {
            var bytesPerSampleFrame = frameSize * AudioChannels * AudioBytesPerSample;
            if (bytesPerSampleFrame <= 0)
            {
                return;
            }

            while (pendingPcm.Length >= bytesPerSampleFrame || (flushFinal && pendingPcm.Length > 0))
            {
                var samplesToEncode = pendingPcm.Length >= bytesPerSampleFrame
                    ? frameSize
                    : checked((int)(pendingPcm.Length / (AudioChannels * AudioBytesPerSample)));

                if (samplesToEncode <= 0)
                {
                    break;
                }

                var bytesToEncode = samplesToEncode * AudioChannels * AudioBytesPerSample;

                ffmpeg.av_frame_unref(encoderFrame);
                encoderFrame->nb_samples = samplesToEncode;
                encoderFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                encoderFrame->sample_rate = AudioSampleRate;
                try
                {
                    ffmpeg.av_channel_layout_default(&encoderFrame->ch_layout, AudioChannels);
                }
                catch (NotSupportedException exception)
                {
                    throw new InvalidOperationException("av_channel_layout_default(encoder_frame) is not supported by the loaded FFmpeg bindings.", exception);
                }
                ffmpeg.av_frame_get_buffer(encoderFrame, 0).ThrowIfError("av_frame_get_buffer(encoder_frame)");
                encoderFrame->pts = encoderSampleCursor;
                encoderSampleCursor += samplesToEncode;

                pendingPcm.CopyToAndConsume((IntPtr)encoderFrame->data[0], bytesToEncode);

                if (!TrySendFrameWithRecovery(encoderContext, encoderFrame, outputContext, outputStream, packet, samplesToEncode, flushFinal))
                {
                    if (!flushFinal)
                    {
                        consecutiveEncoderFrameSendFailures++;

                        if (consecutiveEncoderFrameSendFailures == 1
                            || consecutiveEncoderFrameSendFailures % 12 == 0)
                        {
                            logger.LogWarning(
                                "Encoder send_frame is failing repeatedly for source {SourceId}. ConsecutiveFailures={ConsecutiveFailures}/{MaxConsecutiveFailures}, SamplesToEncode={SamplesToEncode}.",
                                sourceId,
                                consecutiveEncoderFrameSendFailures,
                                maxConsecutiveEncoderFrameSendFailures,
                                samplesToEncode);
                        }

                        if (consecutiveEncoderFrameSendFailures >= maxConsecutiveEncoderFrameSendFailures)
                        {
                            throw new InvalidOperationException(
                                $"Encoder send_frame failed repeatedly for source {sourceId}; aborting capture to allow recovery.");
                        }

                        continue;
                    }

                    throw new InvalidOperationException($"avcodec_send_frame failed for source {sourceId} during final flush after retry.");
                }

                consecutiveEncoderFrameSendFailures = 0;

                while (true)
                {
                    ffmpeg.av_packet_unref(packet);
                    var encodeResult = ffmpeg.avcodec_receive_packet(encoderContext, packet);
                    if (encodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || encodeResult == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    encodeResult.ThrowIfError("avcodec_receive_packet");
                    packet->stream_index = outputStream->index;
                    ffmpeg.av_packet_rescale_ts(packet, encoderContext->time_base, outputStream->time_base);
                    ffmpeg.av_interleaved_write_frame(outputContext, packet).ThrowIfError("av_interleaved_write_frame");
                }
            }
        }

        private bool TrySendFrameWithRecovery(
            AVCodecContext* encoderContext,
            AVFrame* encoderFrame,
            AVFormatContext* outputContext,
            AVStream* outputStream,
            AVPacket* packet,
            int samplesToEncode,
            bool flushFinal)
        {
            var sendResult = ffmpeg.avcodec_send_frame(encoderContext, encoderFrame);
            if (sendResult >= 0)
            {
                return true;
            }

            DrainEncoderPackets(encoderContext, outputContext, outputStream, packet);

            sendResult = ffmpeg.avcodec_send_frame(encoderContext, encoderFrame);
            if (sendResult >= 0)
            {
                return true;
            }

            return false;
        }

        private static void DrainEncoderPackets(AVCodecContext* encoderContext, AVFormatContext* outputContext, AVStream* outputStream, AVPacket* packet)
        {
            while (true)
            {
                ffmpeg.av_packet_unref(packet);
                var result = ffmpeg.avcodec_receive_packet(encoderContext, packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (result < 0)
                {
                    break;
                }

                packet->stream_index = outputStream->index;
                ffmpeg.av_packet_rescale_ts(packet, encoderContext->time_base, outputStream->time_base);
                ffmpeg.av_interleaved_write_frame(outputContext, packet).ThrowIfError("av_interleaved_write_frame(drain)");
            }
        }

        private sealed class PcmByteQueue
        {
            private const int DefaultCapacity = 64 * 1024;
            private const int ShrinkThreshold = 1024 * 1024;

            private byte[] buffer;
            private int startOffset;
            private int length;

            public PcmByteQueue()
            {
                buffer = new byte[DefaultCapacity];
            }

            public int Length => length;

            public void AppendFromFrame(AVFrame* frame, int bytesToAppend)
            {
                if (bytesToAppend <= 0)
                {
                    return;
                }

                EnsureWritable(bytesToAppend);
                Marshal.Copy((IntPtr)frame->data[0], buffer, startOffset + length, bytesToAppend);
                length += bytesToAppend;
            }

            public void CopyToAndConsume(IntPtr destination, int count)
            {
                if (count <= 0)
                {
                    return;
                }

                if (count > length)
                {
                    throw new InvalidOperationException($"PCM buffer underflow. Requested={count}, Available={length}.");
                }

                Marshal.Copy(buffer, startOffset, destination, count);
                startOffset += count;
                length -= count;
                NormalizeAfterConsume();
            }

            public void WriteAllTo(Stream destination)
            {
                if (length == 0)
                {
                    return;
                }

                destination.Write(buffer, startOffset, length);
            }

            public byte[] Snapshot()
            {
                if (length == 0)
                {
                    return [];
                }

                var snapshot = new byte[length];
                Buffer.BlockCopy(buffer, startOffset, snapshot, 0, length);
                return snapshot;
            }

            public void ResetAfterWrite()
            {
                startOffset = 0;
                length = 0;
                MaybeShrink();
            }

            private void EnsureWritable(int additionalBytes)
            {
                if (additionalBytes <= buffer.Length - (startOffset + length))
                {
                    return;
                }

                if (additionalBytes <= buffer.Length - length)
                {
                    Buffer.BlockCopy(buffer, startOffset, buffer, 0, length);
                    startOffset = 0;
                    if (additionalBytes <= buffer.Length - length)
                    {
                        return;
                    }
                }

                var required = checked(length + additionalBytes);
                var newCapacity = Math.Max(DefaultCapacity, buffer.Length);
                while (newCapacity < required)
                {
                    newCapacity = checked(newCapacity * 2);
                }

                var resized = new byte[newCapacity];
                if (length > 0)
                {
                    Buffer.BlockCopy(buffer, startOffset, resized, 0, length);
                }

                buffer = resized;
                startOffset = 0;
            }

            private void NormalizeAfterConsume()
            {
                if (length == 0)
                {
                    startOffset = 0;
                    MaybeShrink();
                    return;
                }

                if (startOffset > buffer.Length / 2)
                {
                    Buffer.BlockCopy(buffer, startOffset, buffer, 0, length);
                    startOffset = 0;
                }
            }

            private void MaybeShrink()
            {
                if (length == 0 && buffer.Length > ShrinkThreshold)
                {
                    buffer = new byte[DefaultCapacity];
                }
            }
        }

        private sealed class ChunkingState
        {
            private readonly int minSamples;
            private readonly int maxSamples;
            private readonly int silenceHoldSamples;
            private readonly int analysisWindowSamples;
            private readonly double adaptiveThresholdMultiplier;
            private readonly double noiseFloorEmaAlpha;
            private readonly double highPassAlpha;

            private int chunkSamples;
            private int silentSamplesAccumulator;
            private int analysisSamples;
            private double analysisSquares;
            private double noiseFloorRms;
            private double previousInput;
            private double previousHighPassOutput;

            private ChunkingState(
                int minSamples,
                int maxSamples,
                int silenceHoldSamples,
                int analysisWindowSamples,
                double adaptiveThresholdMultiplier,
                double noiseFloorEmaAlpha,
                double highPassAlpha)
            {
                this.minSamples = minSamples;
                this.maxSamples = maxSamples;
                this.silenceHoldSamples = silenceHoldSamples;
                this.analysisWindowSamples = analysisWindowSamples;
                this.adaptiveThresholdMultiplier = adaptiveThresholdMultiplier;
                this.noiseFloorEmaAlpha = noiseFloorEmaAlpha;
                this.highPassAlpha = highPassAlpha;
            }

            public static ChunkingState? Create(OperationsWorkerOptions options, ILogger logger, string sourceId)
            {
                var minSeconds = Math.Max(1, options.FlacSilenceMinChunkSeconds);
                var maxSeconds = Math.Max(minSeconds, options.FlacSilenceMaxChunkSeconds);
                var holdMs = Math.Max(1, options.FlacSilenceHoldMilliseconds);
                var windowMs = Math.Max(1, options.FlacSilenceAnalysisWindowMilliseconds);
                var adaptiveThresholdMultiplier = Math.Clamp(options.FlacSilenceAdaptiveThresholdMultiplier, 1.0d, 4.0d);
                var noiseFloorEmaAlpha = Math.Clamp(options.FlacSilenceNoiseFloorEmaAlpha, 0.005d, 0.50d);

                var cutoffHz = Math.Clamp(options.FlacSilenceHighPassCutoffHz, 40d, 600d);
                var dt = 1d / AudioSampleRate;
                var rc = 1d / (2d * Math.PI * cutoffHz);
                var highPassAlpha = rc / (rc + dt);

                return new ChunkingState(
                    minSeconds * AudioSampleRate,
                    maxSeconds * AudioSampleRate,
                    (holdMs * AudioSampleRate) / 1000,
                    (windowMs * AudioSampleRate) / 1000,
                    adaptiveThresholdMultiplier,
                    noiseFloorEmaAlpha,
                    highPassAlpha);
            }

            public ChunkCutDecision Observe(AVFrame* frame)
            {
                if (frame is null || frame->data[0] is null || frame->nb_samples <= 0)
                {
                    return ChunkCutDecision.None;
                }

                var samples = (short*)frame->data[0];
                for (var i = 0; i < frame->nb_samples; i++)
                {
                    var normalized = samples[i] / 32768d;
                    var highPassed = highPassAlpha * (previousHighPassOutput + normalized - previousInput);
                    previousInput = normalized;
                    previousHighPassOutput = highPassed;

                    analysisSquares += highPassed * highPassed;
                    analysisSamples++;
                    chunkSamples++;

                    if (analysisSamples >= analysisWindowSamples)
                    {
                        var rms = Math.Sqrt(analysisSquares / analysisSamples);

                        if (noiseFloorRms <= 0)
                        {
                            noiseFloorRms = rms;
                        }
                        else
                        {
                            var emaAlpha = rms <= noiseFloorRms
                                ? noiseFloorEmaAlpha
                                : noiseFloorEmaAlpha * 0.15d;
                            noiseFloorRms = (1d - emaAlpha) * noiseFloorRms + emaAlpha * rms;
                        }

                        var effectiveThreshold = noiseFloorRms * adaptiveThresholdMultiplier;
                        if (rms <= effectiveThreshold)
                        {
                            silentSamplesAccumulator += analysisSamples;
                        }
                        else
                        {
                            silentSamplesAccumulator = 0;
                        }

                        analysisSamples = 0;
                        analysisSquares = 0;

                        if (chunkSamples >= minSamples && silentSamplesAccumulator >= silenceHoldSamples)
                        {
                            return new ChunkCutDecision(true, false, chunkSamples);
                        }
                    }

                    if (chunkSamples >= maxSamples)
                    {
                        return new ChunkCutDecision(true, true, chunkSamples);
                    }
                }

                return ChunkCutDecision.None;
            }

            public void ResetAfterFlush()
            {
                chunkSamples = 0;
                silentSamplesAccumulator = 0;
                analysisSamples = 0;
                analysisSquares = 0;
                noiseFloorRms = 0;
                previousInput = 0;
                previousHighPassOutput = 0;
            }
        }

        private readonly record struct ChunkCutDecision(bool ShouldCut, bool ForcedByMaxWindow, int ChunkSamples)
        {
            public static ChunkCutDecision None => new(false, false, 0);
        }

    }

    private readonly record struct ChunkTranscriptionRequest(
        string JsonPath,
        string StatsPath,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        IReadOnlyList<byte[]> FlacWindows,
        string SourceId,
        bool ForceFlush);

    private sealed record ChunkTranscriptionItem(string Text, string StartTime, string EndTime);

    private sealed class ChunkTranscriptionPipeline : IDisposable
    {
        private const string DefaultLanguage = "es-CO";
        private const string NoSpeechFallback = "M\u00fasica-NoEspa\u00f1ol.";
        private const int RecognitionMaxOverlapWords = 24;
        private static readonly Meter TranscriptionMeter = new("MediaOpsCore.Workers.Operations.Transcription", "1.0.0");
        private static readonly Counter<long> TranscriptionChunksProcessed = TranscriptionMeter.CreateCounter<long>("media_transcription_chunks_processed");
        private static readonly Counter<long> TranscriptionWindowsProcessed = TranscriptionMeter.CreateCounter<long>("media_transcription_windows_processed");
        private static readonly Counter<long> TranscriptionBoundaryComparisons = TranscriptionMeter.CreateCounter<long>("media_transcription_boundary_comparisons");
        private static readonly Counter<long> TranscriptionBoundaryLossSuspicions = TranscriptionMeter.CreateCounter<long>("media_transcription_boundary_loss_suspicions");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly ConcurrentDictionary<string, SourcePipeline> sourcePipelines = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PathState> pathStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly string googleApiKey;
        private int appendsSinceEviction;
        private int disposed;

        // Per-source ordered channel + single worker. Ensures FIFO processing within a source,
        // preventing out-of-order API responses from creating timestamp gaps in the JSON output.
        private sealed class SourcePipeline(Channel<ChunkTranscriptionRequest> channel, Task worker)
        {
            public readonly Channel<ChunkTranscriptionRequest> Channel = channel;
            public readonly Task Worker = worker;
        }

        public ChunkTranscriptionPipeline(ILogger logger)
        {
            this.logger = logger;
            googleApiKey = ResolveGoogleApiKey();
            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            logger.LogInformation(
                "Transcription windowing configured with chunkOverlap={ChunkOverlapSeconds}s, recognitionWindow={RecognitionWindowSeconds}s, recognitionOverlap={RecognitionOverlapSeconds}s.",
                TranscriptionChunkOverlapSeconds,
                RecognitionWindowSeconds,
                RecognitionWindowOverlapSeconds);
        }

        public void Enqueue(ChunkTranscriptionRequest request)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var pipeline = sourcePipelines.GetOrAdd(request.SourceId, static (id, self) =>
            {
                // Bounded at 64 to back-pressure if transcription falls behind.
                var ch = Channel.CreateBounded<ChunkTranscriptionRequest>(
                    new BoundedChannelOptions(64)
                    {
                        SingleWriter = false,
                        SingleReader = true,
                        AllowSynchronousContinuations = false,
                        FullMode = BoundedChannelFullMode.Wait
                    });
                var worker = Task.Run(() => self.ProcessSourceQueueAsync(ch));
                self.logger.LogInformation("Transcription pipeline created for source {SourceId}.", id);
                return new SourcePipeline(ch, worker);
            }, this);

            try
            {
                pipeline.Channel.Writer.WriteAsync(request, cancellationTokenSource.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to enqueue chunk transcription for source {SourceId} because the queue is unavailable.", request.SourceId);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            // Complete all per-source channels and wait for workers.
            foreach (var (_, pipeline) in sourcePipelines)
            {
                pipeline.Channel.Writer.TryComplete();
            }

            cancellationTokenSource.Cancel();

            try
            {
                Task.WaitAll(sourcePipelines.Values.Select(p => p.Worker).ToArray(), TimeSpan.FromSeconds(30));
            }
            catch
            {
            }

            cancellationTokenSource.Dispose();
            httpClient.Dispose();

            foreach (var state in pathStates.Values)
            {
                state.Lock.Dispose();
            }

            pathStates.Clear();
        }

        private async Task ProcessSourceQueueAsync(Channel<ChunkTranscriptionRequest> sourceChannel)
        {
            try
            {
                await foreach (var request in sourceChannel.Reader.ReadAllAsync(cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    var entryText = string.Empty;
                    var recognitionResult = MergeOutcome.Empty;

                    try
                    {
                        recognitionResult = await RecognizeWindowedAsync(request.FlacWindows, DefaultLanguage).ConfigureAwait(false);
                        entryText = string.IsNullOrWhiteSpace(recognitionResult.Text) ? NoSpeechFallback : recognitionResult.Text;

                        TranscriptionChunksProcessed.Add(1, KeyValuePair.Create<string, object?>("source_id", request.SourceId));
                        TranscriptionWindowsProcessed.Add(request.FlacWindows.Count, KeyValuePair.Create<string, object?>("source_id", request.SourceId));
                        TranscriptionBoundaryComparisons.Add(recognitionResult.BoundaryComparisons, KeyValuePair.Create<string, object?>("source_id", request.SourceId));

                        if (recognitionResult.SuspectedBoundaryLosses > 0)
                        {
                            TranscriptionBoundaryLossSuspicions.Add(recognitionResult.SuspectedBoundaryLosses, KeyValuePair.Create<string, object?>("source_id", request.SourceId));
                            await AppendBoundaryStatsAsync(
                                request.StatsPath,
                                request.SourceId,
                                request.StartTime,
                                request.EndTime,
                                recognitionResult.SuspectedBoundaryLosses,
                                recognitionResult.BoundaryComparisons,
                                request.FlacWindows.Count).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        // On transcription failures, still write the entry with empty text.
                        logger.LogWarning(exception, "Chunk transcription failed for source {SourceId}.", request.SourceId);
                        entryText = string.Empty;
                    }

                    try
                    {
                        var windowEntry = new ChunkTranscriptionItem(
                            entryText,
                            request.StartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                            request.EndTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"));

                        await AppendOrderedAsync(request.JsonPath, windowEntry, request.ForceFlush).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Failed to persist chunk transcription entry for source {SourceId} into {JsonPath}.", request.SourceId, request.JsonPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<MergeOutcome> RecognizeWindowedAsync(IReadOnlyList<byte[]> flacWindows, string language)
        {
            if (flacWindows.Count == 0)
            {
                return MergeOutcome.Empty;
            }

            var segments = new List<string>(flacWindows.Count);
            foreach (var window in flacWindows)
            {
                var segment = await RecognizeSingleAsync(window, language).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    segments.Add(segment);
                }
            }

            return MergeTranscriptSegments(segments);
        }

        private async Task<string> RecognizeSingleAsync(byte[] flacBytes, string language)
        {
            if (flacBytes.Length < 4 || flacBytes[0] != (byte)'f' || flacBytes[1] != (byte)'L' || flacBytes[2] != (byte)'a' || flacBytes[3] != (byte)'C')
            {
                return string.Empty;
            }

            var sampleRate = ReadFlacSampleRate(flacBytes);
            var url = "http://www.google.com/speech-api/v2/recognize"
                + $"?client=chromium&lang={Uri.EscapeDataString(language)}"
                + $"&key={Uri.EscapeDataString(googleApiKey)}&pFilter=0";

            using var content = new ByteArrayContent(flacBytes);
            content.Headers.TryAddWithoutValidation("Content-Type", $"audio/x-flac; rate={sampleRate}");

            using var response = await httpClient.PostAsync(url, content, cancellationTokenSource.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
                {
                    continue;
                }

                var alternatives = result[0].GetProperty("alternative");
                if (alternatives.GetArrayLength() > 0 && alternatives[0].TryGetProperty("transcript", out var transcriptProperty))
                {
                    return transcriptProperty.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static MergeOutcome MergeTranscriptSegments(IReadOnlyList<string> segments)
        {
            if (segments.Count == 0)
            {
                return MergeOutcome.Empty;
            }

            var merged = segments[0].Trim();
            var suspectedBoundaryLosses = 0;
            var boundaryComparisons = 0;
            for (var i = 1; i < segments.Count; i++)
            {
                var next = segments[i].Trim();
                if (string.IsNullOrWhiteSpace(next))
                {
                    continue;
                }

                boundaryComparisons++;
                var pair = MergePairByWordOverlap(merged, next);
                merged = pair.MergedText;
                if (pair.SuspectedBoundaryLoss)
                {
                    suspectedBoundaryLosses++;
                }
            }

            merged = DeduplicateRepeatedPhrases(merged);
            return new MergeOutcome(merged, suspectedBoundaryLosses, boundaryComparisons);
        }

        // Removes repeated phrases of ≥5 words that appear more than once in the merged text.
        // Cross-chunk overlap removal: if the first N words of the incoming chunk already appear
        // at the tail of the previous (chronologically last) chunk, strip them. This removes the
        // duplication produced by TranscriptionChunkOverlapSeconds — consecutive chunks share audio
        // so the API legitimately returns the same words at the boundary.
        // min=5 avoids accidental removals from coincidentally shared short phrases.
        private static string StripChunkPrefixOverlap(
            List<ChunkTranscriptionItem> existingItems,
            string newText,
            int minOverlapWords = 5)
        {
            if (existingItems.Count == 0 || string.IsNullOrWhiteSpace(newText))
            {
                return newText;
            }

            var prev = existingItems[^1];
            return StripTextPrefixOverlap(prev.Text, newText, minOverlapWords);
        }

        // String-based overlap removal used both by StripChunkPrefixOverlap and MergeWindowTexts.
        // Searches for the longest suffix of prevText that matches a contiguous sequence starting
        // at position 0..maxPrefixSkip in newText. If found, returns newText with the duplicate
        // removed (keeping any "skip" words before the match that represent new content the
        // previous chunk missed, plus the remainder after the match).
        private static string StripTextPrefixOverlap(string prevText, string newText, int minOverlapWords = 5)
        {
            if (string.IsNullOrWhiteSpace(prevText) || string.IsNullOrWhiteSpace(newText))
            {
                return newText;
            }

            var prevWords = prevText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (prevWords.Length < minOverlapWords || newWords.Length <= minOverlapWords)
            {
                return newText;
            }

            // Allow the overlap to start at position 0..maxSkip in the new text.
            // skip=0 is the classic prefix strip.
            // skip>0 handles cases where Speech prepends words from the overlap region that
            // the previous chunk didn't capture (different context → different recognition).
            var maxSkip = Math.Min(15, (newWords.Length - minOverlapWords) / 2);

            for (var skip = 0; skip <= maxSkip; skip++)
            {
                var maxOverlap = Math.Min(newWords.Length - skip - 1, prevWords.Length);
                for (var size = maxOverlap; size >= minOverlapWords; size--)
                {
                    var match = true;
                    for (var k = 0; k < size; k++)
                    {
                        if (!NormalizeWord(prevWords[prevWords.Length - size + k])
                                .Equals(NormalizeWord(newWords[skip + k]), StringComparison.Ordinal))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        // Keep words before the overlap (they are new content the previous chunk
                        // didn't catch), skip the duplicate, and keep the remainder after it.
                        var before = skip > 0 ? string.Join(' ', newWords.AsSpan(0, skip)) : string.Empty;
                        var after = string.Join(' ', newWords.AsSpan(skip + size));
                        var result = string.IsNullOrWhiteSpace(before)
                            ? after
                            : string.IsNullOrWhiteSpace(after)
                                ? before
                                : before + " " + after;
                        return string.IsNullOrWhiteSpace(result) ? newText : result;
                    }
                }
            }

            return newText;
        }

        // Merges N buffered window texts into a single string by stripping the cross-window
        // overlap (produced by TranscriptionChunkOverlapSeconds) between consecutive windows.
        // Each window text is already internally deduplicated by MergeTranscriptSegments.
        // We only strip the PCM-overlap boundary prefix here — no phrase dedup across windows,
        // to avoid removing legitimate repetitions common in live radio/sports commentary.
        private static string MergeWindowTexts(List<ChunkTranscriptionItem> windows)
        {
            if (windows.Count == 0)
            {
                return string.Empty;
            }

            if (windows.Count == 1)
            {
                return windows[0].Text;
            }

            var combined = windows[0].Text;
            for (var i = 1; i < windows.Count; i++)
            {
                var stripped = StripTextPrefixOverlap(combined, windows[i].Text, minOverlapWords: 8);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    combined = combined + " " + stripped;
                }
            }

            return combined;
        }

        // Removes repeated phrases of ≥5 words that appear more than once in the merged text.
        // Catches duplicates from overlapping recognition windows where the API returns
        // the same content from adjacent windows that share an overlap region.
        private static string DeduplicateRepeatedPhrases(string text, int minPhraseWords = 5)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= minPhraseWords * 2)
            {
                return text;
            }

            var result = new List<string>(words.Length);
            var i = 0;
            while (i < words.Length)
            {
                var skipped = false;
                var maxLen = Math.Min(minPhraseWords + 20, words.Length - i);
                for (var phraseLen = maxLen; phraseLen >= minPhraseWords; phraseLen--)
                {
                    var lookbackStart = Math.Max(0, result.Count - 120);
                    for (var j = lookbackStart; j <= result.Count - phraseLen; j++)
                    {
                        var match = true;
                        for (var k = 0; k < phraseLen; k++)
                        {
                            if (!NormalizeWord(result[j + k]).Equals(NormalizeWord(words[i + k]), StringComparison.Ordinal))
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            i += phraseLen;
                            skipped = true;
                            break;
                        }
                    }

                    if (skipped)
                    {
                        break;
                    }
                }

                if (!skipped)
                {
                    result.Add(words[i]);
                    i++;
                }
            }

            return string.Join(' ', result);
        }

        private static MergePairOutcome MergePairByWordOverlap(string left, string right)
        {
            var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (leftTokens.Length == 0)
            {
                return new MergePairOutcome(right, 0, false);
            }

            if (rightTokens.Length == 0)
            {
                return new MergePairOutcome(left, 0, false);
            }

            var maxOverlap = Math.Min(RecognitionMaxOverlapWords, Math.Min(leftTokens.Length, rightTokens.Length));
            var overlapWords = 0;

            for (var size = maxOverlap; size >= 1; size--)
            {
                var equal = true;
                for (var i = 0; i < size; i++)
                {
                    var leftWord = NormalizeWord(leftTokens[leftTokens.Length - size + i]);
                    var rightWord = NormalizeWord(rightTokens[i]);
                    if (!leftWord.Equals(rightWord, StringComparison.Ordinal))
                    {
                        equal = false;
                        break;
                    }
                }

                if (equal)
                {
                    overlapWords = size;
                    break;
                }
            }

            if (overlapWords >= rightTokens.Length)
            {
                return new MergePairOutcome(left, overlapWords, false);
            }

            var remainder = string.Join(' ', rightTokens.Skip(overlapWords));
            var mergedText = string.IsNullOrWhiteSpace(remainder)
                ? left
                : string.Concat(left.TrimEnd(), " ", remainder);

            var suspectedBoundaryLoss = overlapWords == 0
                && leftTokens.Length >= 3
                && rightTokens.Length >= 3;

            return new MergePairOutcome(mergedText, overlapWords, suspectedBoundaryLoss);
        }

        private readonly record struct MergeOutcome(string Text, int SuspectedBoundaryLosses, int BoundaryComparisons)
        {
            public static MergeOutcome Empty => new(string.Empty, 0, 0);
        }

        private readonly record struct MergePairOutcome(string MergedText, int OverlapWords, bool SuspectedBoundaryLoss);

        private static string NormalizeWord(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            var chars = token.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return chars.Length == 0 ? string.Empty : new string(chars);
        }

        private static string ResolveGoogleApiKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_API_KEY")?.Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            var fromDotEnv = TryReadGoogleApiKeyFromDotEnv();
            if (!string.IsNullOrWhiteSpace(fromDotEnv))
            {
                return fromDotEnv;
            }

            throw new InvalidOperationException(
                "Missing GOOGLE_SPEECH_API_KEY. Set the environment variable or create a .env file with GOOGLE_SPEECH_API_KEY=<value>.");
        }

        private static string? TryReadGoogleApiKeyFromDotEnv()
        {
            var candidatePaths = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".env")
            }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var path in candidatePaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line[..separatorIndex].Trim();
                    if (!key.Equals("GOOGLE_SPEECH_API_KEY", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static int ResolveConfiguredInt(string key, int fallback, int min, int max)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var parsed))
            {
                return fallback;
            }

            return Math.Clamp(parsed, min, max);
        }

        private async Task AppendOrderedAsync(string jsonPath, ChunkTranscriptionItem windowEntry, bool forceFlush)
        {
            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = pathStates.GetOrAdd(jsonPath, _ => new PathState());
            await state.Lock.WaitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                // Buffer this window; only flush when we have enough or are forced.
                state.PendingWindows ??= [];
                state.PendingWindows.Add(windowEntry);

                if (state.PendingWindows.Count < WindowsPerEntry && !forceFlush)
                {
                    return;
                }

                // Merge buffered windows into one entry.
                var mergedText = MergeWindowTexts(state.PendingWindows);
                var firstStart = state.PendingWindows[0].StartTime;
                var lastEnd = state.PendingWindows[^1].EndTime;
                state.PendingWindows.Clear();

                if (string.IsNullOrWhiteSpace(mergedText))
                {
                    return;
                }

                var entry = new ChunkTranscriptionItem(mergedText, firstStart, lastEnd);

                if (state.CachedItems is null)
                {
                    state.CachedItems = File.Exists(jsonPath)
                        ? JsonSerializer.Deserialize<List<ChunkTranscriptionItem>>(
                            await File.ReadAllTextAsync(jsonPath, cancellationTokenSource.Token).ConfigureAwait(false),
                            JsonOptions) ?? []
                        : [];
                }

                // Strip any prefix of the merged entry that duplicates the tail of the previous entry.
                var dedupedText = StripChunkPrefixOverlap(state.CachedItems, entry.Text, minOverlapWords: 8);
                if (!string.IsNullOrWhiteSpace(dedupedText))
                {
                    entry = entry with { Text = dedupedText };
                }

                state.CachedItems.Add(entry);
                state.CachedItems.Sort(static (a, b) =>
                {
                    var cmp = StringComparer.Ordinal.Compare(a.StartTime, b.StartTime);
                    return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.EndTime, b.EndTime);
                });

                await File.WriteAllTextAsync(
                    jsonPath,
                    JsonSerializer.Serialize(state.CachedItems, JsonOptions),
                    cancellationTokenSource.Token).ConfigureAwait(false);

                state.LastWriteAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                state.Lock.Release();
            }

            if (Interlocked.Increment(ref appendsSinceEviction) % 200 == 0)
            {
                EvictStalePathStates();
            }
        }

        private async Task AppendBoundaryStatsAsync(
            string statsPath,
            string sourceId,
            DateTimeOffset chunkStart,
            DateTimeOffset chunkEnd,
            int suspicions,
            int boundariesCompared,
            int windows)
        {
            var directory = Path.GetDirectoryName(statsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = pathStates.GetOrAdd(statsPath, _ => new PathState());
            await state.Lock.WaitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                List<BoundaryStatsEntry> entries;
                if (state.StatsEntries is null)
                {
                    entries = File.Exists(statsPath)
                        ? JsonSerializer.Deserialize<List<BoundaryStatsEntry>>(
                            await File.ReadAllTextAsync(statsPath, cancellationTokenSource.Token).ConfigureAwait(false),
                            JsonOptions) ?? []
                        : [];
                    state.StatsEntries = entries;
                }
                else
                {
                    entries = state.StatsEntries;
                }

                entries.Add(new BoundaryStatsEntry(
                    sourceId,
                    chunkStart.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    chunkEnd.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    suspicions,
                    boundariesCompared,
                    windows));

                await File.WriteAllTextAsync(
                    statsPath,
                    JsonSerializer.Serialize(entries, JsonOptions),
                    cancellationTokenSource.Token).ConfigureAwait(false);

                state.LastWriteAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                state.Lock.Release();
            }
        }

        private void EvictStalePathStates()
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
            foreach (var state in pathStates.Values)
            {
                // Only evict if not accessed recently; use non-blocking try-enter to avoid stalling.
                if (state.LastWriteAt < cutoff && state.Lock.Wait(0))
                {
                    try
                    {
                        if (state.LastWriteAt < cutoff)
                        {
                            state.CachedItems = null;
                            state.PendingWindows = null;
                            state.StatsEntries = null;
                        }
                    }
                    finally
                    {
                        state.Lock.Release();
                    }
                }
            }
        }

        private static int ReadFlacSampleRate(byte[] flac)
        {
            const int rateOffset = 18;
            if (flac.Length < rateOffset + 3)
            {
                return AudioSampleRate;
            }

            var sampleRate = (flac[rateOffset] << 12)
                | (flac[rateOffset + 1] << 4)
                | (flac[rateOffset + 2] >> 4);

            return sampleRate > 0 ? sampleRate : AudioSampleRate;
        }

        private sealed class PathState
        {
            public readonly SemaphoreSlim Lock = new(1, 1);
            public List<ChunkTranscriptionItem>? CachedItems;
            public List<ChunkTranscriptionItem>? PendingWindows;
            public List<BoundaryStatsEntry>? StatsEntries;
            public DateTimeOffset LastWriteAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed record BoundaryStatsEntry(
        string SourceId,
        string ChunkStart,
        string ChunkEnd,
        int Suspicions,
        int BoundariesCompared,
        int Windows);

}

public static class FfmpegErrorExtensions
{
    public static void ThrowIfError(this int errorCode, string operation)
    {
        if (errorCode >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed with FFmpeg error code {errorCode}.");
    }
}

