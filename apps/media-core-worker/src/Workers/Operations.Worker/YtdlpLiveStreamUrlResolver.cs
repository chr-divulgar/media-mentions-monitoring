using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class YtdlpLiveStreamUrlResolver : ILiveStreamUrlResolver
{
    private static readonly string[] AuthErrorPatterns =
    [
        "sign in to confirm",
        "confirm you're not a bot",
        "this video is only available",
        "http error 403",
        "http error 429",
        "private video",
        "members only",
    ];

    private readonly IYtdlpBinaryProvider binaryProvider;
    private readonly IProcessRunner processRunner;
    private readonly OperationsWorkerOptions options;
    private readonly ILogger<YtdlpLiveStreamUrlResolver> logger;

    public YtdlpLiveStreamUrlResolver(
        IYtdlpBinaryProvider binaryProvider,
        IProcessRunner processRunner,
        OperationsWorkerOptions options,
        ILogger<YtdlpLiveStreamUrlResolver> logger)
    {
        this.binaryProvider = binaryProvider;
        this.processRunner = processRunner;
        this.options = options;
        this.logger = logger;
    }

    public bool CanResolve(CaptureSource source)
        => source.Media.Equals("television", StringComparison.OrdinalIgnoreCase)
        && source.Platform.Equals("youtube", StringComparison.OrdinalIgnoreCase);

    public async Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(
        CaptureSource source,
        CancellationToken cancellationToken = default)
    {
        (string Cmd, string[] Args) runner;
        try
        {
            runner = await binaryProvider.GetCommandAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "[YtdlpResolver] yt-dlp binary unavailable for source {SourceId}.", source.SourceId);
            return new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.BinaryNotFound);
        }

        var args = new List<string>(runner.Args);

        // Cookie guard: if configured, the file is mandatory
        string? tempCookiesPath = null;
        try
        {
            var cookiesPath = options.YoutubeCookiesFilePath;
            if (!string.IsNullOrWhiteSpace(cookiesPath))
            {
                var absPath = Path.IsPathRooted(cookiesPath)
                    ? cookiesPath
                    : Path.GetFullPath(cookiesPath);

                if (!File.Exists(absPath))
                {
                    logger.LogError(
                        "[YtdlpResolver] Cookies file required but not found at '{Path}' for source {SourceId}.",
                        absPath, source.SourceId);
                    return new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.AuthRequired);
                }

                // Convert to Netscape format if needed — yt-dlp rejects raw browser cookie strings
                var effectiveCookiesPath = EnsureNetscapeFormat(absPath, out tempCookiesPath, logger);
                args.AddRange(["--cookies", effectiveCookiesPath]);
                logger.LogDebug("[YtdlpResolver] Using cookies file '{Path}' for source {SourceId}.", effectiveCookiesPath, source.SourceId);
            }

            // Prefer audio-only direct streams over HLS manifests.
            // HLS manifests (m3u8) require FFmpeg to download and analyze segments, which can
            // block avformat_find_stream_info for 10-30s. A direct audio URL (m4a/webm) opens
            // instantly. Format priority: non-HLS audio → known audio-only itags → HLS audio → any.
            const string preferredFormat =
                "bestaudio[protocol!=m3u8][protocol!=m3u8_native]" +
                "/249/250/140/251" +
                "/bestaudio" +
                "/best";
            args.AddRange(["--get-url", "--format", preferredFormat, "--no-playlist", "--quiet", source.StreamUrl]);

            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.YtdlpResolutionTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            ProcessExecutionResult result;
            try
            {
                result = await processRunner.RunAsync(
                    new ProcessCommand(runner.Cmd, args),
                    linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                logger.LogWarning("[YtdlpResolver] Resolution timed out after {Seconds}s for source {SourceId}.",
                    options.YtdlpResolutionTimeoutSeconds, source.SourceId);
                return new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.Unavailable);
            }

            if (!result.Succeeded)
            {
                var failure = ClassifyFailure(result.StandardError);
                logger.LogWarning(
                    "[YtdlpResolver] yt-dlp exited {Code} [{Failure}] for source {SourceId}. Stderr: {Stderr}",
                    result.ExitCode, failure, source.SourceId,
                    result.StandardError?.Trim().Split('\n')[0]);
                return new LiveStreamResolutionResult(null, failure);
            }

            var url = result.StandardOutput?.Trim().Split('\n')[0].Trim();

            if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                logger.LogWarning("[YtdlpResolver] yt-dlp returned invalid URL '{Url}' for source {SourceId}.",
                    url, source.SourceId);
                return new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.Unavailable);
            }

            logger.LogInformation("[YtdlpResolver] Resolved stream URL for {SourceId}: {Url}", source.SourceId, url);
            return new LiveStreamResolutionResult(url);
        }
        finally
        {
            if (tempCookiesPath is not null)
            {
                try { File.Delete(tempCookiesPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    // Ensures the cookies file is in Netscape tab-separated format that yt-dlp accepts.
    // If the file is already Netscape format it is returned as-is.
    // If it looks like a raw browser cookie string (key=value; ...) it is converted to a
    // temporary Netscape file whose path is returned, and tempPath is set for cleanup.
    private static string EnsureNetscapeFormat(
        string absPath,
        out string? tempPath,
        ILogger logger)
    {
        tempPath = null;

        string content;
        try { content = File.ReadAllText(absPath).Trim(); }
        catch { return absPath; }

        // Already Netscape format — use as-is
        if (content.StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
            return absPath;

        // Build Netscape content from raw browser cookie string (key=value; key=value)
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("# Netscape HTTP Cookie File");
        lines.AppendLine("# Converted automatically from browser cookie string");

        foreach (var pair in content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;

            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Domain  domainSpecified  path  secure  expiry  name  value
            lines.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t2147483647\t{name}\t{value}");
        }

        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"yt-cookies-{Guid.NewGuid():N}.txt");
            File.WriteAllText(tempPath, lines.ToString());
            logger.LogDebug("[YtdlpResolver] Converted raw cookie string to Netscape format at '{TempPath}'.", tempPath);
            return tempPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[YtdlpResolver] Failed to write converted cookies temp file. Using original.");
            tempPath = null;
            return absPath;
        }
    }

    private static LiveStreamResolutionFailure ClassifyFailure(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return LiveStreamResolutionFailure.Unavailable;

        var lower = stderr.ToLowerInvariant();
        foreach (var pattern in AuthErrorPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
                return LiveStreamResolutionFailure.AuthRequired;
        }

        return LiveStreamResolutionFailure.Unavailable;
    }
}
