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

        foreach (var source in configuredSources)
        {
            var initialValidation = await streamValidator.ValidateAsync(source.StreamUrl, cancellationToken).ConfigureAwait(false);
            if (initialValidation.Succeeded)
            {
                effectiveSources.Add(source);
                continue;
            }

            logger.LogWarning(
                "Startup validation failed for source {SourceId} streamUrl {StreamUrl}. Error={Error}",
                source.SourceId,
                source.StreamUrl,
                initialValidation.ErrorMessage ?? "n/a");

            if (!options.EnableStartupDiscoveryOnFailedOnly || string.IsNullOrWhiteSpace(source.PrimaryUrl))
            {
                effectiveSources.Add(source);
                continue;
            }

            var discoveredStreamUrl = await sourceDiscoveryService
                .TryResolveStreamUrlAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discoveredStreamUrl))
            {
                logger.LogWarning("Startup discovery could not resolve streamUrl for source {SourceId} from primaryUrl {PrimaryUrl}.", source.SourceId, source.PrimaryUrl);
                effectiveSources.Add(source);
                continue;
            }

            var postDiscoveryValidation = await streamValidator.ValidateAsync(discoveredStreamUrl, cancellationToken).ConfigureAwait(false);
            if (!postDiscoveryValidation.Succeeded)
            {
                logger.LogWarning(
                    "Discovered streamUrl failed validation for source {SourceId}. Candidate={Candidate}. Error={Error}",
                    source.SourceId,
                    discoveredStreamUrl,
                    postDiscoveryValidation.ErrorMessage ?? "n/a");
                effectiveSources.Add(source);
                continue;
            }

            logger.LogInformation(
                "Source {SourceId} recovered by startup discovery. Old streamUrl={OldStreamUrl}, New streamUrl={NewStreamUrl}.",
                source.SourceId,
                source.StreamUrl,
                discoveredStreamUrl);

            effectiveSources.Add(source.WithStreamUrl(discoveredStreamUrl));

            var persisted = await captureSourceProvider
                .PersistStreamUrlAsync(source.SourceId, discoveredStreamUrl, cancellationToken)
                .ConfigureAwait(false);

            if (persisted)
            {
                logger.LogInformation(
                    "Persisted discovered streamUrl for source {SourceId} into capture sources file.",
                    source.SourceId);
            }
        }

        captureSourceProvider.SetResolvedSources(effectiveSources);
        logger.LogInformation("Startup source validation completed. Effective sources: {SourceCount}.", effectiveSources.Count);
    }
}
