using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace MediaOpsCore.Workers.Operations;

public sealed class FfmpegStartupStreamValidator : IStartupStreamValidator
{
    private static readonly string[] RequiredFfmpegLibraries = ["avutil", "avcodec", "avformat", "swresample"];
    private const string DefaultHttpUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";
    private const int MaxValidationAttempts = 3;
    private static int ffmpegInitialized;
    private readonly OperationsWorkerOptions options;

    public FfmpegStartupStreamValidator(OperationsWorkerOptions options)
    {
        this.options = options;
        EnsureFfmpegInitialized();
    }

    public async Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return new StartupStreamValidationResult(false, "Stream URL is empty.");
        }

        var timeoutSeconds = Math.Max(2, options.StartupValidationTimeoutSeconds);
        StartupStreamValidationResult? lastResult = null;

        for (var attempt = 1; attempt <= MaxValidationAttempts; attempt++)
        {
            try
            {
                var result = await Task
                    .Run(() => ValidateCore(streamUrl), cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    return result;
                }

                lastResult = result;
                if (attempt >= MaxValidationAttempts || !IsRetryableFailure(result.ErrorMessage))
                {
                    return result;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lastResult = new StartupStreamValidationResult(false, $"Validation timed out after {timeoutSeconds}s.");
                if (attempt >= MaxValidationAttempts)
                {
                    return lastResult;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new StartupStreamValidationResult(false, "Validation canceled.");
            }
            catch (Exception exception)
            {
                lastResult = new StartupStreamValidationResult(false, exception.Message);
                if (attempt >= MaxValidationAttempts)
                {
                    return lastResult;
                }
            }
        }

        return lastResult ?? new StartupStreamValidationResult(false, "Validation failed with unknown error.");
    }

    private unsafe StartupStreamValidationResult ValidateCore(string streamUrl)
    {
        AVFormatContext* inputContext = null;
        AVDictionary* inputOptions = null;
        AVPacket* packet = null;
        var isHttpStream = streamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || streamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        try
        {
            ApplyBaseStartupInputOptions(&inputOptions);

            var openResult = ffmpeg.avformat_open_input(&inputContext, streamUrl, null, &inputOptions);
            if (openResult < 0 && isHttpStream)
            {
                if (inputContext is not null)
                {
                    ffmpeg.avformat_close_input(&inputContext);
                }

                ffmpeg.av_dict_free(&inputOptions);

                ApplyBaseStartupInputOptions(&inputOptions);
                ffmpeg.av_dict_set(&inputOptions, "user_agent", DefaultHttpUserAgent, 0);

                var headers = BuildHttpRequestHeaders(streamUrl);
                if (!string.IsNullOrWhiteSpace(headers))
                {
                    ffmpeg.av_dict_set(&inputOptions, "headers", headers, 0);
                }

                openResult = ffmpeg.avformat_open_input(&inputContext, streamUrl, null, &inputOptions);
            }

            if (openResult < 0)
            {
                return new StartupStreamValidationResult(false, $"avformat_open_input failed with FFmpeg error code {openResult}.");
            }

            var infoResult = ffmpeg.avformat_find_stream_info(inputContext, null);
            if (infoResult < 0)
            {
                return new StartupStreamValidationResult(false, $"avformat_find_stream_info failed with FFmpeg error code {infoResult}.");
            }

            AVCodec* decoder = null;
            var audioStreamIndex = ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (audioStreamIndex < 0)
            {
                return new StartupStreamValidationResult(false, "No audio stream found.");
            }

            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                return new StartupStreamValidationResult(false, "Unable to allocate FFmpeg packet for startup probe.");
            }

            var hasReadableAudioPacket = false;
            for (var attempt = 0; attempt < 64; attempt++)
            {
                ffmpeg.av_packet_unref(packet);
                var readResult = ffmpeg.av_read_frame(inputContext, packet);
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (readResult < 0)
                {
                    return new StartupStreamValidationResult(false, $"av_read_frame failed during startup probe with FFmpeg error code {readResult}.");
                }

                if (packet->stream_index == audioStreamIndex)
                {
                    hasReadableAudioPacket = true;
                    break;
                }
            }

            if (!hasReadableAudioPacket)
            {
                return new StartupStreamValidationResult(false, "No readable audio packets found during startup probe.");
            }

            return new StartupStreamValidationResult(true);
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            ffmpeg.av_dict_free(&inputOptions);

            if (inputContext is not null)
            {
                ffmpeg.avformat_close_input(&inputContext);
            }
        }
    }

    private unsafe void ApplyBaseStartupInputOptions(AVDictionary** inputOptions)
    {
        ffmpeg.av_dict_set(inputOptions, "stimeout", "5000000", 0);
        ffmpeg.av_dict_set(inputOptions, "rw_timeout", "5000000", 0);
        ffmpeg.av_dict_set(inputOptions, "probesize", "524288", 0);
        ffmpeg.av_dict_set(inputOptions, "analyzeduration", "5000000", 0);
        ffmpeg.av_dict_set(inputOptions, "reconnect", "1", 0);
        ffmpeg.av_dict_set(inputOptions, "reconnect_streamed", "1", 0);
        ffmpeg.av_dict_set(inputOptions, "reconnect_delay_max", Math.Max(1, options.DecoderReconnectDelayMaxSeconds).ToString(), 0);
    }

    private static bool IsRetryableFailure(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return true;
        }

        return errorMessage.Contains("avformat_open_input failed", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("av_read_frame failed", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("No audio stream found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("No readable audio packets", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase);
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

    private static void EnsureFfmpegInitialized()
    {
        if (Interlocked.Exchange(ref ffmpegInitialized, 1) != 0)
        {
            return;
        }

        var rootPath = ResolveFfmpegRootPath();
        ffmpeg.RootPath = rootPath;

        FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.Initialize();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
        _ = ffmpeg.avformat_version();
    }

    private static string ResolveFfmpegRootPath()
    {
        var embeddedDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        if (!ContainsRequiredFfmpegLibraries(embeddedDirectory))
        {
            throw new InvalidOperationException($"No embedded FFmpeg shared libraries were found in '{embeddedDirectory}'.");
        }

        return embeddedDirectory;
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
}
