using System.Collections.Concurrent;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class SourceAvailabilityReconciliationService : BackgroundService, ICaptureAttemptObserver, IYouTubeReconciliationTrigger
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
    // Resolved sources found with a stopped session on the previous tick — see RecoverSilentlyStoppedSourcesAsync.
    private IReadOnlySet<string> missingOnPreviousTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    // Same circular-resolution reason as AudioCapturePlugin: the reader is the plugin itself.
    private ILiveCaptureProgressReader LiveCaptureProgressReader => serviceProvider.GetRequiredService<ILiveCaptureProgressReader>();

    public Task ReportAsync(CaptureSource source, AudioCaptureExecutionResult result, CancellationToken cancellationToken = default)
    {
        if (result.Succeeded)
        {
            inFlightHotRecovery.TryRemove(source.SourceId, out _);
            return Task.CompletedTask;
        }

        if (result.ExcludeSource)
        {
            // Hot recovery would just re-validate the same reachable URL and record the same
            // unusable content again. Exclude now; the scheduled reconciliation retries later.
            _ = Task.Run(() => ExcludeSourceAsync(source, result.ErrorMessage), CancellationToken.None);
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
                await RecoverSilentlyStoppedSourcesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Check for silently stopped capture sessions failed.");
            }

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

    // Safety net for any session that stops without reporting a failure: the source stays in the
    // resolved list, so scheduled reconciliation (which only looks at non-resolved sources) never
    // retries it and it stays down for good. In-memory only — no Firestore traffic — so it runs on
    // every tick.
    private async Task RecoverSilentlyStoppedSourcesAsync(CancellationToken cancellationToken)
    {
        // Same filtered set ContinuousCaptureUseCase starts sessions for (media allow-list, canary)
        // — a resolved source outside it never gets a session and must not be "recovered" forever.
        var resolved = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);
        var (toRecover, stillMissing) = FindSilentlyStoppedSources(
            resolved.Select(source => source.SourceId).ToArray(),
            LiveCaptureProgressReader.StoppedSourceIds,
            inFlightHotRecovery.Keys.ToArray(),
            missingOnPreviousTick);
        missingOnPreviousTick = stillMissing;

        foreach (var source in resolved.Where(source => toRecover.Contains(source.SourceId)))
        {
            if (!inFlightHotRecovery.TryAdd(source.SourceId, 0))
            {
                continue;
            }

            logger.LogWarning(
                "Source {SourceId} is resolved but its capture session stopped and nothing restarted it; starting hot recovery.",
                source.SourceId);
            _ = Task.Run(() => TryHotRecoverUntilRotationAsync(source), CancellationToken.None);
        }
    }

    /// <summary>
    /// Pure decision for RecoverSilentlyStoppedSourcesAsync. Only sources that HAD a session which
    /// stopped count — a source with no session yet belongs to startup or scheduled reconciliation
    /// (keying off "not running" instead raced startup validation, which takes over a minute). A
    /// stopped session is also only acted on once it is still stopped on the next tick with no hot
    /// recovery in flight: a recovery that just succeeded replaces the session a moment later, and a
    /// failure reported while another recovery was running is dropped and must be caught here.
    /// </summary>
    internal static (IReadOnlySet<string> ToRecover, IReadOnlySet<string> StillMissing) FindSilentlyStoppedSources(
        IReadOnlyCollection<string> resolvedSourceIds,
        IReadOnlyCollection<string> stoppedSourceIds,
        IReadOnlyCollection<string> inFlightRecoveryIds,
        IReadOnlySet<string> missingOnPreviousTick)
    {
        var stopped = stoppedSourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inFlight = inFlightRecoveryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = resolvedSourceIds
            .Where(id => stopped.Contains(id) && !inFlight.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRecover = missing
            .Where(missingOnPreviousTick.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (toRecover, missing);
    }

    // The current streamUrl comes first, same order as startup validation. BuildConservativeCandidates
    // excludes it by design, so recovering from variants alone could never bring back a stream that
    // simply dropped — it stayed down until the service restarted.
    internal static IReadOnlyList<string> BuildRadioRecoveryCandidates(CaptureSource source) =>
        [source.StreamUrl, .. StartupStreamUrlHeuristics.BuildConservativeCandidates(source)];

    // Last retry slot of the rotation window the failure happened in. Computed once up front:
    // checking `Minute == 59` on each attempt missed the slot whenever an attempt spanned the whole
    // of minute :59, and the loop kept going for another full hour.
    internal static DateTimeOffset ResolveHotRecoveryDeadline(DateTimeOffset sourceNow) =>
        new(sourceNow.Year, sourceNow.Month, sourceNow.Day, sourceNow.Hour, 59, 0, sourceNow.Offset);

    private async Task ExcludeSourceAsync(CaptureSource source, string? reason)
    {
        try
        {
            captureSourceProvider.RemoveResolvedSource(source.SourceId);
            await captureSourceProvider
                .PersistExclusionAsync(source.SourceId, true, CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogWarning(
                "Source {SourceId} excluded without hot recovery: its stream content is unusable. StreamUrl={StreamUrl}. Reason={Reason}",
                source.SourceId, source.StreamUrl, reason?.Split('\n')[0]);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to exclude source {SourceId}.", source.SourceId);
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
            var deadline = ResolveHotRecoveryDeadline(DateTimeOffset.UtcNow.ToOffset(sourceOffset));
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
                if (sourceNow >= deadline)
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

    // Invoked when fresh cookies arrive via YouTubeCookiesHttpService — recovers currently-excluded
    // YouTube sources right away instead of waiting for the next scheduled tick (minute 0/1/30/59)
    // or an in-flight hot-recovery loop's next per-minute attempt.
    public async Task<int> TriggerImmediateReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var configuredSources = await captureSourceProvider.ListConfiguredSourcesAsync(cancellationToken).ConfigureAwait(false);

        var resolvedIds = captureSourceProvider
            .ListResolvedSources()
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excludedTvSources = configuredSources
            .Where(source => liveStreamUrlResolver.CanResolve(source)
                          && !resolvedIds.Contains(source.SourceId)
                          && !inFlightHotRecovery.ContainsKey(source.SourceId))
            .ToArray();

        var recoveredCount = 0;

        foreach (var source in excludedTvSources)
        {
            var recovered = await TryRecoverSourceAsync(source, cancellationToken).ConfigureAwait(false);
            if (recovered is null)
            {
                continue;
            }

            captureSourceProvider.AddOrUpdateResolvedSource(recovered);
            await captureSourceProvider
                .PersistStreamUrlAsync(recovered.SourceId, recovered.StreamUrl, cancellationToken)
                .ConfigureAwait(false);
            await captureSourceProvider
                .PersistExclusionAsync(recovered.SourceId, false, cancellationToken)
                .ConfigureAwait(false);

            recoveredCount++;
            _ = Task.Run(() => TriggerCaptureAsync(recovered), CancellationToken.None);
        }

        logger.LogInformation(
            "Immediate YouTube reconciliation finished. Recovered={RecoveredCount} of {AttemptedCount} excluded TV source(s).",
            recoveredCount, excludedTvSources.Length);

        return recoveredCount;
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

        // ── Radio/video: the current streamUrl, then conservative structural variants ──
        var candidates = BuildRadioRecoveryCandidates(source);

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
