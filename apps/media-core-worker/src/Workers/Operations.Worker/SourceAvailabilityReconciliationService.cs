using System.Collections.Concurrent;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class SourceAvailabilityReconciliationService : BackgroundService, ICaptureAttemptObserver
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly HashSet<int> ScheduledReconciliationMinutes = [1, 30, 59];

    private readonly OperationsWorkerOptions options;
    private readonly StaticCaptureSourceProvider captureSourceProvider;
    private readonly IStartupStreamValidator streamValidator;
    private readonly IStartupSourceDiscoveryService sourceDiscoveryService;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly IAudioCapturePlugin audioCapturePlugin;
    private readonly ILogger<SourceAvailabilityReconciliationService> logger;

    private readonly ConcurrentDictionary<string, byte> inFlightHotRecovery = new(StringComparer.OrdinalIgnoreCase);

    public SourceAvailabilityReconciliationService(
        OperationsWorkerOptions options,
        StaticCaptureSourceProvider captureSourceProvider,
        IStartupStreamValidator streamValidator,
        IStartupSourceDiscoveryService sourceDiscoveryService,
        IIngestionPluginResolver pluginResolver,
        IAudioCapturePlugin audioCapturePlugin,
        ILogger<SourceAvailabilityReconciliationService> logger)
    {
        this.options = options;
        this.captureSourceProvider = captureSourceProvider;
        this.streamValidator = streamValidator;
        this.sourceDiscoveryService = sourceDiscoveryService;
        this.pluginResolver = pluginResolver;
        this.audioCapturePlugin = audioCapturePlugin;
        this.logger = logger;
    }

    public Task ReportAsync(CaptureSource source, AudioCaptureExecutionResult result, CancellationToken cancellationToken = default)
    {
        if (result.Succeeded)
        {
            inFlightHotRecovery.TryRemove(source.SourceId, out _);
            return Task.CompletedTask;
        }

        if (!inFlightHotRecovery.TryAdd(source.SourceId, 0))
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(() => TryHotRecoverOnceAsync(source), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Source availability reconciliation service started. Scheduled checks at minutes {Minutes}.", string.Join(",", ScheduledReconciliationMinutes.OrderBy(value => value)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileExcludedAtScheduledMinutesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Source availability reconciliation cycle failed.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TryHotRecoverOnceAsync(CaptureSource failedSource)
    {
        try
        {
            captureSourceProvider.RemoveResolvedSource(failedSource.SourceId);

            var recovered = await TryRecoverSourceAsync(failedSource, CancellationToken.None).ConfigureAwait(false);
            if (recovered is null)
            {
                return;
            }

            captureSourceProvider.AddOrUpdateResolvedSource(recovered);
            await PersistRecoveredStreamUrlAsync(failedSource, recovered.StreamUrl, CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation(
                "Hot recovery succeeded for source {SourceId}. StreamUrl={StreamUrl}",
                recovered.SourceId,
                recovered.StreamUrl);

            _ = Task.Run(() => TriggerCaptureAsync(recovered), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hot recovery failed unexpectedly for source {SourceId}.", failedSource.SourceId);
        }
        finally
        {
            inFlightHotRecovery.TryRemove(failedSource.SourceId, out _);
        }
    }

    private async Task ReconcileExcludedAtScheduledMinutesAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (!ScheduledReconciliationMinutes.Contains(nowUtc.Minute))
        {
            return;
        }

        var configuredSources = await captureSourceProvider.ListConfiguredSourcesAsync(cancellationToken).ConfigureAwait(false);
        var resolvedIds = captureSourceProvider
            .ListResolvedSources()
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excludedSources = configuredSources
            .Where(source => !resolvedIds.Contains(source.SourceId))
            .ToArray();

        if (excludedSources.Length == 0)
        {
            logger.LogInformation("Scheduled excluded-source reconciliation at minute {Minute} finished. Recovered=0, StillExcluded=0.", nowUtc.Minute);
            return;
        }

        var recoveredIds = new List<string>();
        var stillExcludedIds = new List<string>();

        foreach (var source in excludedSources)
        {
            var recovered = await TryRecoverSourceAsync(source, cancellationToken).ConfigureAwait(false);
            if (recovered is null)
            {
                stillExcludedIds.Add(source.SourceId);
                continue;
            }

            captureSourceProvider.AddOrUpdateResolvedSource(recovered);
            await PersistRecoveredStreamUrlAsync(source, recovered.StreamUrl, cancellationToken).ConfigureAwait(false);
            recoveredIds.Add(source.SourceId);
        }

        logger.LogInformation(
            "Scheduled excluded-source reconciliation at minute {Minute} finished. Recovered={RecoveredCount} [{RecoveredIds}] StillExcluded={StillExcludedCount} [{StillExcludedIds}]",
            nowUtc.Minute,
            recoveredIds.Count,
            string.Join(",", recoveredIds),
            stillExcludedIds.Count,
            string.Join(",", stillExcludedIds));
    }

    private async Task TriggerCaptureAsync(CaptureSource source)
    {
        try
        {
            var plan = await pluginResolver
                .ResolveAsync(source, IngestionMode.Continuous, CancellationToken.None)
                .ConfigureAwait(false);

            await audioCapturePlugin
                .CaptureAsync(source, plan, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to trigger immediate capture for recovered source {SourceId}.", source.SourceId);
        }
    }

    private async Task<CaptureSource?> TryRecoverSourceAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        if (!options.EnableStartupDiscoveryOnFailedOnly || string.IsNullOrWhiteSpace(source.PrimaryUrl))
        {
            return null;
        }

        var discoveredCandidates = await sourceDiscoveryService
            .DiscoverStreamUrlsAsync(source, cancellationToken)
            .ConfigureAwait(false);

        if (discoveredCandidates.Count == 0)
        {
            return null;
        }

        var validationResults = await Task.WhenAll(discoveredCandidates.Select(async candidate =>
        {
            var validation = await streamValidator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            return (Candidate: candidate, Validation: validation);
        })).ConfigureAwait(false);

        var successfulCandidate = validationResults.FirstOrDefault(result => result.Validation.Succeeded);
        if (!string.IsNullOrWhiteSpace(successfulCandidate.Candidate))
        {
            return source.WithStreamUrl(successfulCandidate.Candidate);
        }

        return null;
    }

    private async Task PersistRecoveredStreamUrlAsync(CaptureSource originalSource, string recoveredStreamUrl, CancellationToken cancellationToken)
    {
        await captureSourceProvider
            .PersistStreamUrlAsync(originalSource.SourceId, recoveredStreamUrl, cancellationToken)
            .ConfigureAwait(false);
    }
}
