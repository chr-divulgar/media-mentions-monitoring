using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Point-in-time snapshot of YouTube cookie/authentication health, combining the auth alert
/// flag, cookie file validation, and the current set of excluded YouTube (TV) sources.
/// Exposed over HTTP via <see cref="YouTubeCookiesHttpService"/> so NestJS can tell a live,
/// reachable worker apart from stale local state.
/// </summary>
public sealed record YouTubeHealthSnapshot(
    bool AuthAlertActive,
    CookiesValidationResult CookiesValidation,
    IReadOnlyList<string> ExcludedTvSourceIds,
    int TotalTvSourceCount,
    int ActiveTvSourceCount);

public interface IYouTubeHealthSnapshotProvider
{
    Task<YouTubeHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class YouTubeHealthSnapshotProvider : IYouTubeHealthSnapshotProvider
{
    private readonly OperationsWorkerOptions options;
    private readonly IYouTubeCookiesAlertService alertService;
    private readonly IYouTubeCookiesValidator cookiesValidator;
    private readonly StaticCaptureSourceProvider captureSourceProvider;
    private readonly ILiveStreamUrlResolver liveStreamUrlResolver;

    public YouTubeHealthSnapshotProvider(
        OperationsWorkerOptions options,
        IYouTubeCookiesAlertService alertService,
        IYouTubeCookiesValidator cookiesValidator,
        StaticCaptureSourceProvider captureSourceProvider,
        ILiveStreamUrlResolver liveStreamUrlResolver)
    {
        this.options = options;
        this.alertService = alertService;
        this.cookiesValidator = cookiesValidator;
        this.captureSourceProvider = captureSourceProvider;
        this.liveStreamUrlResolver = liveStreamUrlResolver;
    }

    public async Task<YouTubeHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var authAlertActive = alertService.AlertExists();
        var validation = await cookiesValidator
            .ValidateCookiesAsync(options.YoutubeCookiesFilePath)
            .ConfigureAwait(false);

        var configuredSources = await captureSourceProvider
            .ListConfiguredSourcesAsync(cancellationToken)
            .ConfigureAwait(false);

        var tvSources = configuredSources
            .Where(source => liveStreamUrlResolver.CanResolve(source))
            .ToArray();

        var resolvedIds = captureSourceProvider
            .ListResolvedSources()
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excludedTvSourceIds = tvSources
            .Where(source => !resolvedIds.Contains(source.SourceId))
            .Select(source => source.SourceId)
            .ToArray();

        return new YouTubeHealthSnapshot(
            AuthAlertActive: authAlertActive,
            CookiesValidation: validation,
            ExcludedTvSourceIds: excludedTvSourceIds,
            TotalTvSourceCount: tvSources.Length,
            ActiveTvSourceCount: tvSources.Length - excludedTvSourceIds.Length);
    }
}
