using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace MediaOpsCore.Workers.Operations;

public sealed class FfmpegStartupStreamValidator : IStartupStreamValidator
{
    private static readonly string[] RequiredFfmpegLibraries = ["avutil", "avcodec", "avformat", "swresample"];
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

        try
        {
            return await Task
                .Run(() => ValidateCore(streamUrl), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new StartupStreamValidationResult(false, $"Validation timed out after {timeoutSeconds}s.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StartupStreamValidationResult(false, "Validation canceled.");
        }
        catch (Exception exception)
        {
            return new StartupStreamValidationResult(false, exception.Message);
        }
    }

    private static unsafe StartupStreamValidationResult ValidateCore(string streamUrl)
    {
        AVFormatContext* inputContext = null;
        AVDictionary* inputOptions = null;

        try
        {
            ffmpeg.av_dict_set(&inputOptions, "stimeout", "5000000", 0);
            ffmpeg.av_dict_set(&inputOptions, "probesize", "131072", 0);
            ffmpeg.av_dict_set(&inputOptions, "analyzeduration", "1000000", 0);

            var openResult = ffmpeg.avformat_open_input(&inputContext, streamUrl, null, &inputOptions);
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

            return new StartupStreamValidationResult(true);
        }
        finally
        {
            ffmpeg.av_dict_free(&inputOptions);

            if (inputContext is not null)
            {
                ffmpeg.avformat_close_input(&inputContext);
            }
        }
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
