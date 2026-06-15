using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class StartupSourceInitializationService : IStartupSourceInitializationService
{
    private readonly OperationsWorkerOptions options;
    private readonly StaticCaptureSourceProvider captureSourceProvider;
    private readonly IStartupStreamValidator streamValidator;
    private readonly IStartupSourceDiscoveryService sourceDiscoveryService;
    private readonly ILiveStreamUrlResolver liveStreamUrlResolver;
    private readonly IYouTubeCookiesAlertService cookiesAlertService;
    private readonly ILogger<StartupSourceInitializationService> logger;

    public StartupSourceInitializationService(
        OperationsWorkerOptions options,
        StaticCaptureSourceProvider captureSourceProvider,
        IStartupStreamValidator streamValidator,
        IStartupSourceDiscoveryService sourceDiscoveryService,
        ILiveStreamUrlResolver liveStreamUrlResolver,
        IYouTubeCookiesAlertService cookiesAlertService,
        ILogger<StartupSourceInitializationService> logger)
    {
        this.options = options;
        this.captureSourceProvider = captureSourceProvider;
        this.streamValidator = streamValidator;
        this.sourceDiscoveryService = sourceDiscoveryService;
        this.liveStreamUrlResolver = liveStreamUrlResolver;
        this.cookiesAlertService = cookiesAlertService;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!options.EnableStartupValidation)
        {
            logger.LogInformation("Startup source validation is disabled; using configured streamUrl values.");
            return;
        }

        var configuredSources = await captureSourceProvider.ListConfiguredSourcesAsync(cancellationToken).ConfigureAwait(false);
        var effectiveSources = new List<CaptureSource>(configuredSources.Count);

        logger.LogInformation("Startup source validation started for {SourceCount} sources.", configuredSources.Count);

        // ── Pre-resolution: resolve live stream URLs for television sources before FFmpeg validation ──
        var sourcesToValidate = new List<CaptureSource>(configuredSources.Count);
        var tvReadySourceIds = new List<string>();
        var tvExcludedSourceIds = new List<(string SourceId, string Reason)>();

        foreach (var source in configuredSources)
        {
            if (!liveStreamUrlResolver.CanResolve(source))
            {
                sourcesToValidate.Add(source);
                continue;
            }

            logger.LogInformation("Resolving YouTube live stream URL for TV source {SourceId}...", source.SourceId);
            var resolution = await liveStreamUrlResolver
                .TryResolveStreamUrlAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (!resolution.Succeeded)
            {
                var reason = resolution.Failure == LiveStreamResolutionFailure.AuthRequired
                    ? "auth-required"
                    : resolution.Failure?.ToString().ToLowerInvariant() ?? "unavailable";

                if (resolution.Failure == LiveStreamResolutionFailure.AuthRequired)
                {
                    cookiesAlertService.WriteAlert(source.SourceId,
                        "Authentication required at startup. Cookies may be missing or expired.");
                    logger.LogError(
                        "TV source {SourceId} excluded — YouTube authentication required. " +
                        "Renew cookies and delete '{FlagPath}' to resume.",
                        source.SourceId, cookiesAlertService.AlertFilePath);
                }
                else
                {
                    logger.LogWarning(
                        "YouTube stream URL resolution failed for source {SourceId} [{Failure}]. Marking excluded.",
                        source.SourceId, resolution.Failure);
                }

                tvExcludedSourceIds.Add((source.SourceId, reason));
                await captureSourceProvider
                    .PersistExclusionAsync(source.SourceId, true, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (cookiesAlertService.AlertExists())
                cookiesAlertService.ClearAlert();

            tvReadySourceIds.Add(source.SourceId);
            await captureSourceProvider
                .PersistStreamUrlAsync(source.SourceId, resolution.Url!, cancellationToken)
                .ConfigureAwait(false);

            sourcesToValidate.Add(source.WithStreamUrl(resolution.Url!));
        }

        var initialValidationTasks = sourcesToValidate
            .Select(source => streamValidator.ValidateAsync(source.StreamUrl, cancellationToken))
            .ToArray();

        var initialValidationResults = await Task.WhenAll(initialValidationTasks).ConfigureAwait(false);

        var validSourceIds = new List<string>(sourcesToValidate.Count);
        var invalidSourceIds = new List<string>();

        for (var index = 0; index < sourcesToValidate.Count; index++)
        {
            var source = sourcesToValidate[index];
            var initialValidation = initialValidationResults[index];

            // Always run fallback discovery in the background for sources with a primaryUrl,
            // regardless of whether they already have fallbacks or their validation status.
            // This keeps the fallbackStreamUrls list up to date without blocking capture.
            if (!string.IsNullOrWhiteSpace(source.PrimaryUrl))
            {
                _ = DiscoverAndPersistFallbacksAsync(source, cancellationToken);
            }

            if (initialValidation.Succeeded)
            {
                // Primary streamUrl is valid and capture can start immediately.
                // If the source was previously excluded, clear the flag now that it is reachable.
                if (source.IsExcluded)
                {
                    await captureSourceProvider
                        .PersistExclusionAsync(source.SourceId, false, cancellationToken)
                        .ConfigureAwait(false);
                }

                effectiveSources.Add(source.WithExcluded(false));
                validSourceIds.Add(source.SourceId);
                continue;
            }

            // streamUrl failed — try conservative structural variants from fallbackStreamUrls.
            // Only fallbacks with the same host+path as the current streamUrl are considered safe
            // (scheme toggle or token rotation). Different-host fallbacks are skipped.
            var candidates = StartupStreamUrlHeuristics.BuildConservativeCandidates(source);

            if (candidates.Count > 0)
            {
                var validationResults = await Task.WhenAll(candidates.Select(async candidate =>
                {
                    var validation = await streamValidator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                    return (Candidate: candidate, Validation: validation);
                })).ConfigureAwait(false);

                var first = validationResults.FirstOrDefault(r => r.Validation.Succeeded);
                if (!string.IsNullOrWhiteSpace(first.Candidate))
                {
                    effectiveSources.Add(source.WithStreamUrl(first.Candidate).WithExcluded(false));
                    validSourceIds.Add(source.SourceId);
                    await captureSourceProvider
                        .PersistStreamUrlAsync(source.SourceId, first.Candidate, cancellationToken)
                        .ConfigureAwait(false);
                    await captureSourceProvider
                        .PersistExclusionAsync(source.SourceId, false, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            // No structural variant works — mark as excluded so it is skipped on the next
            // restart and is visible in the JSON for diagnosis and manual correction.
            await captureSourceProvider
                .PersistExclusionAsync(source.SourceId, true, cancellationToken)
                .ConfigureAwait(false);
            invalidSourceIds.Add(source.SourceId);
        }

        captureSourceProvider.SetResolvedSources(effectiveSources);

        // Merge TV results into the summary so the final line reflects all sources.
        // TV sources that resolved successfully are already counted in validSourceIds
        // (they went through the FFmpeg validation loop). tvReadySourceIds is only used
        // to detect whether TV sources should NOT appear in tvExcludedSourceIds.
        var allValidIds = validSourceIds.ToList();
        var allInvalidIds = invalidSourceIds
            .Select(id => $"{id}(stream-unavailable)")
            .Concat(tvExcludedSourceIds.Select(t => $"{t.SourceId}({t.Reason})"))
            .ToList();

        logger.LogInformation(
            "Startup source validation/discovery finished. " +
            "Valid={ValidCount} [{ValidSourceIds}] " +
            "Excluded={InvalidCount} [{InvalidSourceIds}]",
            allValidIds.Count,
            string.Join(",", allValidIds),
            allInvalidIds.Count,
            string.Join(",", allInvalidIds));
    }

    // Fire-and-forget: discovers fallback URLs for a source whose primary URL is already valid.
    // Runs after startup completes so it never delays capture.
    private async Task DiscoverAndPersistFallbacksAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await sourceDiscoveryService
                .DiscoverStreamUrlsAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return;
            }

            var validationResults = await Task.WhenAll(candidates.Select(async candidate =>
            {
                var validation = await streamValidator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                return (Candidate: candidate, Validation: validation);
            })).ConfigureAwait(false);

            var fallbackUrls = validationResults
                .Where(r => r.Validation.Succeeded
                    && !string.Equals(r.Candidate, source.StreamUrl, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Candidate)
                .ToArray();

            if (fallbackUrls.Length > 0)
            {
                await captureSourceProvider
                    .PersistFallbackStreamUrlsAsync(source.SourceId, fallbackUrls, cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Discovered {Count} fallback URL(s) for source {SourceId}: {Urls}",
                    fallbackUrls.Length,
                    source.SourceId,
                    string.Join(", ", fallbackUrls));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fallback discovery failed silently for source {SourceId}.", source.SourceId);
        }
    }
}
