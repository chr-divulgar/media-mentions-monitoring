using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class InProcessFfmpegAudioCapturePlugin : IAudioCapturePlugin, IDisposable
{
    private const int AudioSampleRate = 16000;
    private const int AudioChannels = 1;
    private const int AudioBytesPerSample = 2;
    private const int WavHeaderSize = 44;
    private static readonly string[] RequiredFfmpegLibraries = ["avutil", "avcodec", "avformat", "swresample"];

    private static int ffmpegInitialized;
    private int disposed;
    private readonly OperationsWorkerOptions options;
    private readonly ILogger<InProcessFfmpegAudioCapturePlugin> logger;
    private readonly ConcurrentDictionary<string, CaptureSession> sessions = new(StringComparer.Ordinal);

    public InProcessFfmpegAudioCapturePlugin(OperationsWorkerOptions options, ILogger<InProcessFfmpegAudioCapturePlugin> logger)
    {
        this.options = options;
        this.logger = logger;
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
        var sourceDirectory = Path.Combine(options.AudioOutputRootPath, mediaDirectory, source.SourceId);
        Directory.CreateDirectory(sourceDirectory);

        var session = sessions.AddOrUpdate(
            source.SourceId,
            _ => CaptureSession.Start(source, sourceDirectory, plan, options, logger),
            (_, existing) => existing.IsRunning ? existing : CaptureSession.Start(source, sourceDirectory, plan, options, logger));

        var startupResult = await session.WaitForStartupAsync(cancellationToken).ConfigureAwait(false);
        if (!startupResult.Succeeded)
        {
            return new AudioCaptureExecutionResult(false, startupResult.OpusFilePath, startupResult.ErrorMessage);
        }

        if (!session.IsRunning)
        {
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

    private static DateTimeOffset AlignWindow(DateTimeOffset now, TimeSpan window)
    {
        var ticks = window.Ticks;
        var alignedTicks = (now.UtcTicks / ticks) * ticks;
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
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
        private readonly string sourceDirectory;
        private readonly PluginExecutionPlan plan;
        private readonly CaptureSource source;
        private readonly OperationsWorkerOptions options;
        private readonly ILogger logger;
        private volatile string? activeOpusPath;
        private volatile bool isRunning;
        private volatile string? lastError;

        private CaptureSession(
            CaptureSource source,
            string sourceDirectory,
            PluginExecutionPlan plan,
            OperationsWorkerOptions options,
            ILogger logger)
        {
            this.source = source;
            this.sourceDirectory = sourceDirectory;
            this.plan = plan;
            this.options = options;
            this.logger = logger;
            sourceId = source.SourceId;
            isRunning = true;
            captureTask = Task.Run(RunAsync);
        }

        public static CaptureSession Start(
            CaptureSource source,
            string sourceDirectory,
            PluginExecutionPlan plan,
            OperationsWorkerOptions options,
            ILogger logger)
        {
            return new CaptureSession(source, sourceDirectory, plan, options, logger);
        }

        public bool IsRunning => isRunning && !captureTask.IsCompleted;

        public string? LastError => lastError;

        public string CurrentOpusPath() => CurrentOpusPath(DateTimeOffset.UtcNow);

        public string CurrentOpusPath(DateTimeOffset now)
        {
            return CurrentOpusPath(now, plan.OpusRotationInterval);
        }

        public string CurrentOpusPath(DateTimeOffset now, TimeSpan rotationInterval)
        {
            var windowStart = AlignWindow(now, rotationInterval);
            var suffix = rotationInterval >= TimeSpan.FromHours(1)
                ? windowStart.ToString("yyyyMMdd_HH")
                : rotationInterval >= TimeSpan.FromMinutes(1)
                    ? windowStart.ToString("yyyyMMdd_HHmm")
                    : windowStart.ToString("yyyyMMdd_HHmmss");

            return Path.Combine(sourceDirectory, $"{sourceId}_{suffix}.opus");
        }

        public string CurrentWavPath(DateTimeOffset now)
        {
            var windowStart = AlignWindow(now, plan.WavWindowDuration);
            return Path.Combine(sourceDirectory, $"{sourceId}_{windowStart:yyyyMMdd_HHmmss}.wav");
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
            MemoryStream? pendingOpusPcm = null;
            MemoryStream? pendingWavPcm = null;
            var sampleCursor = 0L;
            var encoderSampleCursor = 0L;
            var lastFlushAt = DateTimeOffset.UtcNow;
            var effectiveOpusRotationInterval = TimeSpan.FromHours(1);
            var nextWavWindowAt = AlignWindow(lastFlushAt, plan.WavWindowDuration).Add(plan.WavWindowDuration);
            var nextRotationAt = AlignWindow(lastFlushAt, effectiveOpusRotationInterval).Add(effectiveOpusRotationInterval);
            var encoderFrameSize = 0;
            WavChunkingState? wavChunkingState = null;

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

                ffmpeg.avformat_open_input(&inputContextPtr, source.StreamUrl, null, &inputOptions).ThrowIfError("avformat_open_input");
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
                pendingOpusPcm = new MemoryStream();
                pendingWavPcm = new MemoryStream();
                wavChunkingState = options.EnableWavSilenceChunking
                    ? WavChunkingState.Create(options, logger, sourceId)
                    : null;

                activeOpusPath = CurrentOpusPath(DateTimeOffset.UtcNow, effectiveOpusRotationInterval);
                outputContext = OpenOutputContext(activeOpusPath, encoderContext, ref outputStream);
                startupCompletionSource.TrySetResult(new AudioCaptureExecutionResult(true, activeOpusPath));
                logger.LogInformation("Capture started for source {SourceId}. Reconnect={ReconnectEnabled}, RtspTcp={RtspPreferTcp}, WavSilenceChunking={SilenceChunkingEnabled}, OpusBitrateKbps={OpusBitrateKbps}.", sourceId, options.EnableDecoderReconnect, options.RtspPreferTcp, wavChunkingState is not null, options.DefaultOpusBitrateKbps);
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
                        break;
                    }

                    readResult.ThrowIfError("av_read_frame");

                    if (inputPacket->stream_index != audioStreamIndex)
                    {
                        continue;
                    }

                    ffmpeg.avcodec_send_packet(decoderContext, inputPacket).ThrowIfError("avcodec_send_packet");

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
                        sampleCursor += resampledFrame->nb_samples;

                        var now = DateTimeOffset.UtcNow;

                        AppendSamples(pendingOpusPcm!, resampledFrame, AudioChannels);
                        EncodeBufferedSamples(pendingOpusPcm!, encoderFrameSize, encoderContext, encoderFrame, outputContext, outputStream, outputPacket, ref encoderSampleCursor);

                        AppendSamples(pendingWavPcm!, resampledFrame, AudioChannels);
                        if (wavChunkingState is not null)
                        {
                            var cutDecision = wavChunkingState.Observe(resampledFrame);
                            if (cutDecision.ShouldCut)
                            {
                                FlushWavWindow(pendingWavPcm!, CurrentWavPath(now));
                                logger.LogDebug("WAV chunk cut for source {SourceId}. ForcedByMaxWindow={ForcedByMaxWindow}, ChunkSamples={ChunkSamples}.", sourceId, cutDecision.ForcedByMaxWindow, cutDecision.ChunkSamples);
                                wavChunkingState.ResetAfterFlush();
                            }
                        }
                        else if (now >= nextWavWindowAt)
                        {
                            FlushWavWindow(pendingWavPcm!, CurrentWavPath(now));
                            nextWavWindowAt = AlignWindow(now, plan.WavWindowDuration).Add(plan.WavWindowDuration);
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

                        if (now >= nextRotationAt)
                        {
                            RotateOutput(ref outputContext, ref outputStream, encoderContext, outputPacket, now, effectiveOpusRotationInterval);
                            nextRotationAt = AlignWindow(now, effectiveOpusRotationInterval).Add(effectiveOpusRotationInterval);
                        }
                    }
                }

                if (pendingOpusPcm is not null && pendingOpusPcm.Length > 0)
                {
                    EncodeBufferedSamples(pendingOpusPcm, int.MaxValue, encoderContext, encoderFrame!, outputContext, outputStream, outputPacket, ref encoderSampleCursor, flushFinal: true);
                }

                DrainEncoder(encoderContext, outputContext, outputStream, outputPacket);

                if (pendingWavPcm is not null && pendingWavPcm.Length > 0)
                {
                    FlushWavWindow(pendingWavPcm, CurrentWavPath(DateTimeOffset.UtcNow));
                }
                isRunning = false;
                logger.LogInformation("Capture completed for source {SourceId}.", sourceId);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                var detailedError = exception.ToString();
                SetFailure(detailedError);
                logger.LogError(exception, "Capture failed for source {SourceId}.", sourceId);
                startupCompletionSource.TrySetResult(new AudioCaptureExecutionResult(false, activeOpusPath ?? CurrentOpusPath(), detailedError));
                return Task.CompletedTask;
            }
            finally
            {
                isRunning = false;

                pendingOpusPcm?.Dispose();
                pendingWavPcm?.Dispose();

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

        private static void DrainEncoder(AVCodecContext* encoderContext, AVFormatContext* outputContext, AVStream* outputStream, AVPacket* packet)
        {
            ffmpeg.avcodec_send_frame(encoderContext, null).ThrowIfError("avcodec_send_frame(flush)");

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

        private static void AppendSamples(MemoryStream pendingPcm, AVFrame* frame, int channels)
        {
            var pcmBytes = checked((int)(frame->nb_samples * channels * AudioBytesPerSample));
            var span = new ReadOnlySpan<byte>(frame->data[0], pcmBytes);
            pendingPcm.Write(span);
        }

        private static void FlushWavWindow(MemoryStream pcmBuffer, string outputPath)
        {
            if (pcmBuffer.Length == 0)
            {
                return;
            }

            var pcmBytes = pcmBuffer.ToArray();
            pcmBuffer.SetLength(0);

            var wavBytes = new byte[WavHeaderSize + pcmBytes.Length];

            var bytesPerSecond = AudioSampleRate * AudioChannels * AudioBytesPerSample;
            WriteAscii(wavBytes, 0, "RIFF");
            BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(4, 4), 36 + pcmBytes.Length);
            WriteAscii(wavBytes, 8, "WAVE");
            WriteAscii(wavBytes, 12, "fmt ");
            BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(22, 2), (short)AudioChannels);
            BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(24, 4), AudioSampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(28, 4), bytesPerSecond);
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(32, 2), (short)(AudioChannels * AudioBytesPerSample));
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(34, 2), 16);
            WriteAscii(wavBytes, 36, "data");
            BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(40, 4), pcmBytes.Length);
            pcmBytes.CopyTo(wavBytes, WavHeaderSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, wavBytes);
        }

        private static void WriteAscii(Span<byte> destination, int offset, string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                destination[offset + index] = (byte)value[index];
            }
        }

        private static void EncodeBufferedSamples(
            MemoryStream pendingPcm,
            int frameSize,
            AVCodecContext* encoderContext,
            AVFrame* encoderFrame,
            AVFormatContext* outputContext,
            AVStream* outputStream,
            AVPacket* packet,
            ref long encoderSampleCursor,
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
                var pcm = pendingPcm.ToArray();
                var frameBytes = new byte[bytesToEncode];
                Buffer.BlockCopy(pcm, 0, frameBytes, 0, bytesToEncode);

                var remainingBytes = pcm.Length - bytesToEncode;
                pendingPcm.SetLength(0);
                if (remainingBytes > 0)
                {
                    pendingPcm.Write(pcm, bytesToEncode, remainingBytes);
                }

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

                Marshal.Copy(frameBytes, 0, (IntPtr)encoderFrame->data[0], bytesToEncode);
                ffmpeg.avcodec_send_frame(encoderContext, encoderFrame).ThrowIfError("avcodec_send_frame");

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

        private sealed class WavChunkingState
        {
            private readonly int minSamples;
            private readonly int maxSamples;
            private readonly int silenceHoldSamples;
            private readonly int analysisWindowSamples;
            private readonly double thresholdLinear;

            private int chunkSamples;
            private int silentSamplesAccumulator;
            private int analysisSamples;
            private double analysisSquares;

            private WavChunkingState(int minSamples, int maxSamples, int silenceHoldSamples, int analysisWindowSamples, double thresholdLinear)
            {
                this.minSamples = minSamples;
                this.maxSamples = maxSamples;
                this.silenceHoldSamples = silenceHoldSamples;
                this.analysisWindowSamples = analysisWindowSamples;
                this.thresholdLinear = thresholdLinear;
            }

            public static WavChunkingState? Create(OperationsWorkerOptions options, ILogger logger, string sourceId)
            {
                var minSeconds = Math.Max(1, options.WavSilenceMinChunkSeconds);
                var maxSeconds = Math.Max(minSeconds, options.WavSilenceMaxChunkSeconds);
                var holdMs = Math.Max(1, options.WavSilenceHoldMilliseconds);
                var windowMs = Math.Max(1, options.WavSilenceAnalysisWindowMilliseconds);
                var thresholdLinear = Math.Clamp(Math.Pow(10d, options.WavSilenceThresholdDb / 20d), 1e-6, 1d);

                return new WavChunkingState(
                    minSeconds * AudioSampleRate,
                    maxSeconds * AudioSampleRate,
                    (holdMs * AudioSampleRate) / 1000,
                    (windowMs * AudioSampleRate) / 1000,
                    thresholdLinear);
            }

            public WavCutDecision Observe(AVFrame* frame)
            {
                if (frame is null || frame->data[0] is null || frame->nb_samples <= 0)
                {
                    return WavCutDecision.None;
                }

                var samples = (short*)frame->data[0];
                for (var i = 0; i < frame->nb_samples; i++)
                {
                    var normalized = samples[i] / 32768d;
                    analysisSquares += normalized * normalized;
                    analysisSamples++;
                    chunkSamples++;

                    if (analysisSamples >= analysisWindowSamples)
                    {
                        var rms = Math.Sqrt(analysisSquares / analysisSamples);
                        if (rms <= thresholdLinear)
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
                            return new WavCutDecision(true, false, chunkSamples);
                        }
                    }

                    if (chunkSamples >= maxSamples)
                    {
                        return new WavCutDecision(true, true, chunkSamples);
                    }
                }

                return WavCutDecision.None;
            }

            public void ResetAfterFlush()
            {
                chunkSamples = 0;
                silentSamplesAccumulator = 0;
                analysisSamples = 0;
                analysisSquares = 0;
            }
        }

        private readonly record struct WavCutDecision(bool ShouldCut, bool ForcedByMaxWindow, int ChunkSamples)
        {
            public static WavCutDecision None => new(false, false, 0);
        }

    }

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

