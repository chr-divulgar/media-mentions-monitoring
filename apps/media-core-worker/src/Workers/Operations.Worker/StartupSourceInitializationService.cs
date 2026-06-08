using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class StartupSourceInitializationService : IStartupSourceInitializationService
{
    private readonly OperationsWorkerOptions options;
    private readonly StaticCaptureSourceProvider captureSourceProvider;
    private readonly IStartupStreamValidator streamValidator;
    private readonly IStartupSourceDiscoveryService sourceDiscoveryService;
    private readonly ILogger<StartupSourceInitializationService> logger;

    public StartupSourceInitializationService(
        OperationsWorkerOptions options,
        StaticCaptureSourceProvider captureSourceProvider,
        IStartupStreamValidator streamValidator,
        IStartupSourceDiscoveryService sourceDiscoveryService,
        ILogger<StartupSourceInitializationService> logger)
    {
        this.options = options;
        this.captureSourceProvider = captureSourceProvider;
        this.streamValidator = streamValidator;
        this.sourceDiscoveryService = sourceDiscoveryService;
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

        var initialValidationTasks = configuredSources
            .Select(source => streamValidator.ValidateAsync(source.StreamUrl, cancellationToken))
            .ToArray();

        var initialValidationResults = await Task.WhenAll(initialValidationTasks).ConfigureAwait(false);

        var validSourceIds = new List<string>(configuredSources.Count);
        var invalidSourceIds = new List<string>();

        for (var index = 0; index < configuredSources.Count; index++)
        {
            var source = configuredSources[index];
            var initialValidation = initialValidationResults[index];
            if (initialValidation.Succeeded)
            {
                // Primary URL is valid. If fallbacks are not yet populated and discovery is enabled,
                // run discovery silently to find alternate URLs and persist them — without blocking capture.
                if (source.FallbackStreamUrls.Count == 0
                    && options.EnableStartupDiscoveryOnFailedOnly
                    && !string.IsNullOrWhiteSpace(source.PrimaryUrl))
                {
                    _ = DiscoverAndPersistFallbacksAsync(source, cancellationToken);
                }

                effectiveSources.Add(source);
                validSourceIds.Add(source.SourceId);
                continue;
            }

            if (!options.EnableStartupDiscoveryOnFailedOnly || string.IsNullOrWhiteSpace(source.PrimaryUrl))
            {
                invalidSourceIds.Add(source.SourceId);
                continue;
            }

            var discoveredCandidates = await sourceDiscoveryService
                .DiscoverStreamUrlsAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (discoveredCandidates.Count == 0)
            {
                invalidSourceIds.Add(source.SourceId);
                continue;
            }

            var validationResults = await Task.WhenAll(discoveredCandidates.Select(async candidate =>
            {
                var validation = await streamValidator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                return (Candidate: candidate, Validation: validation);
            })).ConfigureAwait(false);

            var succeededCandidates = validationResults
                .Where(result => result.Validation.Succeeded)
                .Select(result => result.Candidate)
                .ToArray();

            var discoveredStreamUrl = succeededCandidates.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(discoveredStreamUrl))
            {
                invalidSourceIds.Add(source.SourceId);
                continue;
            }

            var fallbackUrls = succeededCandidates.Skip(1).ToArray();

            effectiveSources.Add(source.WithStreamUrl(discoveredStreamUrl).WithFallbackStreamUrls(fallbackUrls));
            validSourceIds.Add(source.SourceId);

            await captureSourceProvider
                .PersistStreamUrlAsync(source.SourceId, discoveredStreamUrl, cancellationToken)
                .ConfigureAwait(false);

            if (fallbackUrls.Length > 0)
            {
                await captureSourceProvider
                    .PersistFallbackStreamUrlsAsync(source.SourceId, fallbackUrls, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        captureSourceProvider.SetResolvedSources(effectiveSources);
        logger.LogInformation(
            "Startup source validation/discovery finished. Valid={ValidCount} [{ValidSourceIds}] Invalid={InvalidCount} [{InvalidSourceIds}]",
            validSourceIds.Count,
            string.Join(",", validSourceIds),
            invalidSourceIds.Count,
            string.Join(",", invalidSourceIds));
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
