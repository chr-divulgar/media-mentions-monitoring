using System.Collections.Concurrent;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class SourceAvailabilityReconciliationService : BackgroundService, ICaptureAttemptObserver
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    // Minute 0 is included so that sources excluded at :59 are retried at the very
    // start of the next hour rather than waiting until :01 or :30.
    private static readonly HashSet<int> ScheduledReconciliationMinutes = [0, 1, 30, 59];

    private readonly StaticCaptureSourceProvider captureSourceProvider;
    private readonly IStartupStreamValidator streamValidator;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly IServiceProvider serviceProvider;
    private readonly ILiveStreamUrlResolver liveStreamUrlResolver;
    private readonly IYouTubeCookiesAlertService cookiesAlertService;
    private readonly ILogger<SourceAvailabilityReconciliationService> logger;

    private readonly ConcurrentDictionary<string, byte> inFlightHotRecovery = new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken serviceStopping = CancellationToken.None;

    public SourceAvailabilityReconciliationService(
        StaticCaptureSourceProvider captureSourceProvider,
        IStartupStreamValidator streamValidator,
        IIngestionPluginResolver pluginResolver,
        IServiceProvider serviceProvider,
        ILiveStreamUrlResolver liveStreamUrlResolver,
        IYouTubeCookiesAlertService cookiesAlertService,
        ILogger<SourceAvailabilityReconciliationService> logger)
    {
        this.captureSourceProvider = captureSourceProvider;
        this.streamValidator = streamValidator;
        this.pluginResolver = pluginResolver;
        this.serviceProvider = serviceProvider;
        this.liveStreamUrlResolver = liveStreamUrlResolver;
        this.cookiesAlertService = cookiesAlertService;
        this.logger = logger;
    }

    // Resolved lazily: this service is the ICaptureAttemptObserver that the plugin factory
    // requires, so taking IAudioCapturePlugin in the constructor creates a circular DI
    // resolution (plugin → observer → plugin) that deadlocks at first resolution.
    // The plugin is only needed long after startup, when TriggerCaptureAsync runs.
    private IAudioCapturePlugin AudioCapturePlugin => serviceProvider.GetRequiredService<IAudioCapturePlugin>();

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

        _ = Task.Run(() => TryHotRecoverUntilRotationAsync(source), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStopping = stoppingToken;
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

    // Retries recovery at each exact minute mark (:23:00, :24:00, ...) until the source
    // comes back OR minute :59 of the current hour is reached. At :59 marks excluded and
    // stops — scheduled reconciliation at :00 of the next hour picks it up immediately.
    private async Task TryHotRecoverUntilRotationAsync(CaptureSource failedSource)
    {
        try
        {
            captureSourceProvider.RemoveResolvedSource(failedSource.SourceId);

            var sourceOffset = TimeSpan.FromMinutes(failedSource.UtcOffsetMinutes);
            var attempt = 0;

            while (!serviceStopping.IsCancellationRequested)
            {
                attempt++;

                // Validate conservative fallbacks — no exclusion persisted on each failed attempt.
                var recovered = await TryRecoverSourceAsync(failedSource, CancellationToken.None, persistExclusionOnFailure: false)
                    .ConfigureAwait(false);

                if (recovered is not null)
                {
                    captureSourceProvider.AddOrUpdateResolvedSource(recovered);
                    await captureSourceProvider
                        .PersistStreamUrlAsync(recovered.SourceId, recovered.StreamUrl, CancellationToken.None)
                        .ConfigureAwait(false);
                    await captureSourceProvider
                        .PersistExclusionAsync(recovered.SourceId, false, CancellationToken.None)
                        .ConfigureAwait(false);

                    var isTv = liveStreamUrlResolver.CanResolve(failedSource);
                    logger.LogInformation(
                        "Hot recovery succeeded for source {SourceId} [{MediaType}] on attempt {Attempt}. StreamUrl={StreamUrl}",
                        recovered.SourceId,
                        isTv ? $"{failedSource.Media}/{failedSource.Platform}" : failedSource.Media,
                        attempt,
                        recovered.StreamUrl);

                    _ = Task.Run(() => TriggerCaptureAsync(recovered), CancellationToken.None);
                    return;
                }

                var sourceNow = DateTimeOffset.UtcNow.ToOffset(sourceOffset);

                // Minute :59 is the last retry slot for this rotation window.
                // Mark excluded so the reconciliation at :00 of the next hour can pick it up.
                if (sourceNow.Minute == 59)
                {
                    await captureSourceProvider
                        .PersistExclusionAsync(failedSource.SourceId, true, CancellationToken.None)
                        .ConfigureAwait(false);

                    logger.LogWarning(
                        "Hot recovery exhausted for source {SourceId} after {Attempt} attempt(s) at minute :59. Marked excluded; reconciliation at :00 will retry.",
                        failedSource.SourceId, attempt);
                    return;
                }

                // Wait until the next exact minute boundary (:XX:00) rather than a relative delay.
                // Example: failed at 13:22:30 → next attempt at 13:23:00 (30 s wait).
                var delay = DelayUntilNextMinuteBoundary(sourceNow);
                logger.LogDebug(
                    "Hot recovery attempt {Attempt} failed for source {SourceId}. Next attempt at :{NextMinute:D2}.",
                    attempt, failedSource.SourceId, sourceNow.Minute + 1);

                try
                {
                    await Task.Delay(delay, serviceStopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hot recovery loop failed unexpectedly for source {SourceId}.", failedSource.SourceId);
        }
        finally
        {
            inFlightHotRecovery.TryRemove(failedSource.SourceId, out _);
        }
    }

    // Returns the time remaining until the next :00 second of the next minute.
    // E.g. now=13:22:30 → 30 s; now=13:22:00 → 60 s.
    private static TimeSpan DelayUntilNextMinuteBoundary(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset)
            .AddMinutes(1);
        var delay = next - now;
        return delay.TotalSeconds < 1 ? TimeSpan.FromMinutes(1) : delay;
    }

    private async Task ReconcileExcludedAtScheduledMinutesAsync(CancellationToken cancellationToken)
    {
        var configuredSources = await captureSourceProvider.ListConfiguredSourcesAsync(cancellationToken).ConfigureAwait(false);
        var operationalNow = ResolveOperationalNow(configuredSources);
        if (!ScheduledReconciliationMinutes.Contains(operationalNow.Minute))
        {
            return;
        }

        var resolvedIds = captureSourceProvider
            .ListResolvedSources()
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Skip sources that already have a hot-recovery loop running to avoid double-recovery.
        var excludedSources = configuredSources
            .Where(source => !resolvedIds.Contains(source.SourceId)
                          && !inFlightHotRecovery.ContainsKey(source.SourceId))
            .ToArray();

        if (excludedSources.Length == 0)
        {
            logger.LogInformation("Scheduled excluded-source reconciliation at minute {Minute} finished. Recovered=0, StillExcluded=0.", operationalNow.Minute);
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
            await captureSourceProvider
                .PersistStreamUrlAsync(recovered.SourceId, recovered.StreamUrl, cancellationToken)
                .ConfigureAwait(false);
            await captureSourceProvider
                .PersistExclusionAsync(recovered.SourceId, false, cancellationToken)
                .ConfigureAwait(false);
            recoveredIds.Add(source.SourceId);
            _ = Task.Run(() => TriggerCaptureAsync(recovered), CancellationToken.None);
        }

        logger.LogInformation(
            "Scheduled excluded-source reconciliation at minute {Minute} finished. Recovered={RecoveredCount} [{RecoveredIds}] StillExcluded={StillExcludedCount} [{StillExcludedIds}]",
            operationalNow.Minute,
            recoveredIds.Count,
            string.Join(",", recoveredIds),
            stillExcludedIds.Count,
            string.Join(",", stillExcludedIds));

        // Emit a TV-specific status board whenever TV sources are involved so operators can track them at a glance
        var tvResolved = captureSourceProvider.ListResolvedSources()
            .Where(s => liveStreamUrlResolver.CanResolve(s))
            .Select(s => s.SourceId)
            .ToList();
        var tvExcluded = configuredSources
            .Where(s => liveStreamUrlResolver.CanResolve(s) && !tvResolved.Contains(s.SourceId, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.SourceId)
            .ToList();

        if (tvResolved.Count > 0 || tvExcluded.Count > 0)
        {
            var authAlertActive = cookiesAlertService.AlertExists();
            logger.LogInformation(
                "TV source status — Active={ActiveCount} [{ActiveIds}] Excluded={ExcludedCount} [{ExcludedIds}]{AuthAlert}",
                tvResolved.Count,
                string.Join(",", tvResolved),
                tvExcluded.Count,
                string.Join(",", tvExcluded),
                authAlertActive ? " [AUTH ALERT ACTIVE — renew cookies]" : "");
        }
    }

    private static DateTimeOffset ResolveOperationalNow(IReadOnlyList<CaptureSource> configuredSources)
    {
        if (configuredSources.Count == 0)
        {
            return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5));
        }

        var offset = TimeSpan.FromMinutes(configuredSources[0].UtcOffsetMinutes);
        return DateTimeOffset.UtcNow.ToOffset(offset);
    }

    private async Task TriggerCaptureAsync(CaptureSource source)
    {
        try
        {
            var plan = await pluginResolver
                .ResolveAsync(source, IngestionMode.Continuous, CancellationToken.None)
                .ConfigureAwait(false);

            await AudioCapturePlugin
                .CaptureAsync(source, plan, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to trigger immediate capture for recovered source {SourceId}.", source.SourceId);
        }
    }

    private async Task<CaptureSource?> TryRecoverSourceAsync(CaptureSource source, CancellationToken cancellationToken, bool persistExclusionOnFailure = true)
    {
        // ── Television/YouTube: re-resolve ephemeral HLS URL via yt-dlp ──
        if (liveStreamUrlResolver.CanResolve(source))
        {
            // Auth alert active: operator has not renewed cookies yet — skip, wait.
            if (cookiesAlertService.AlertExists())
            {
                logger.LogDebug(
                    "Skipping TV recovery for {SourceId}: YouTube auth alert active. " +
                    "Waiting for operator to renew cookies and delete the flag.",
                    source.SourceId);
                return null;
            }

            var resolution = await liveStreamUrlResolver
                .TryResolveStreamUrlAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (resolution.Succeeded)
            {
                cookiesAlertService.ClearAlert();
                return source.WithStreamUrl(resolution.Url!).WithExcluded(false);
            }

            if (resolution.Failure == LiveStreamResolutionFailure.AuthRequired)
            {
                cookiesAlertService.WriteAlert(source.SourceId,
                    "Authentication failed during recovery. Cookies expired or invalid.");
                logger.LogError(
                    "TV source {SourceId} — YouTube authentication required during recovery. " +
                    "Hot-recovery suspended until operator renews cookies and deletes the flag.",
                    source.SourceId);
            }

            if (persistExclusionOnFailure)
            {
                await captureSourceProvider
                    .PersistExclusionAsync(source.SourceId, true, cancellationToken)
                    .ConfigureAwait(false);
            }

            return null;
        }

        // ── Radio/video: conservative structural variants (scheme toggle, token rotation) ──
        var candidates = StartupStreamUrlHeuristics.BuildConservativeCandidates(source);

        foreach (var candidate in candidates)
        {
            var validation = await streamValidator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (validation.Succeeded)
            {
                return source.WithStreamUrl(candidate).WithExcluded(false);
            }
        }

        if (persistExclusionOnFailure)
        {
            await captureSourceProvider
                .PersistExclusionAsync(source.SourceId, true, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }
}
