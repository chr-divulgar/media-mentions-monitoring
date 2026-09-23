using MediaOpsCore.BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Keeps the coverage record of the hour being recorded, and archives each hour once it closes.
///
/// Two separate concerns, deliberately on different storage:
/// - Local, every few minutes: a capture artifact is only emitted at rotation, so without this the
///   in-progress hour exists nowhere but the sessions' memory and a worker stopped mid-hour loses
///   it. A file write costs nothing, so this can be frequent.
/// - Firestore, once per hour: the audit trail. Written only when the hour closes, with every
///   source in a single document, so the database is touched 24 times a day and never read.
///
/// Neither path reads the source catalog — the plugin already knows which sources are recording.
/// </summary>
public sealed class CaptureSummaryArchiveWorker : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const double WindowSeconds = 3600.0;

    private readonly ILiveCaptureProgressReader liveCaptureProgressReader;
    private readonly StageMirrorMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly FirestoreCaptureSummaryStore summaryStore;
    private readonly ILogger<CaptureSummaryArchiveWorker> logger;

    private string? lastSeenHourKey;

    public CaptureSummaryArchiveWorker(
        ILiveCaptureProgressReader liveCaptureProgressReader,
        StageMirrorMonitoringArtifactRepository monitoringArtifactRepository,
        IEvidenceFileStore evidenceFileStore,
        FirestoreCaptureSummaryStore summaryStore,
        ILogger<CaptureSummaryArchiveWorker> logger)
    {
        this.liveCaptureProgressReader = liveCaptureProgressReader;
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.evidenceFileStore = evidenceFileStore;
        this.summaryStore = summaryStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The archive is a side record — a failed write must never stop capture.
                logger.LogWarning(ex, "[CaptureSummaryArchiveWorker] Archive tick failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var nowBogota = DateTimeOffset.UtcNow.ToOffset(BogotaOffset);
        var hourKey = $"{nowBogota:yyyy-MM-dd_HH}";

        if (lastSeenHourKey is not null && lastSeenHourKey != hourKey)
        {
            await ArchiveClosedHourAsync(lastSeenHourKey, cancellationToken).ConfigureAwait(false);
        }

        lastSeenHourKey = hourKey;
        await SnapshotInProgressHourAsync(hourKey, nowBogota, cancellationToken).ConfigureAwait(false);
    }

    private async Task SnapshotInProgressHourAsync(string hourKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Once the rotation artifacts land, the hour has authoritative numbers — a live snapshot
        // taken in the seconds between rotation and the clock hour would overwrite them with the
        // new window's counters, which have already reset to zero.
        if (monitoringArtifactRepository.TryPeekHour(hourKey) is not null)
        {
            return;
        }

        var entries = BuildLiveEntries(now, InProcessFfmpegAudioCapturePlugin.AlignWindow(now, TimeSpan.FromHours(1)));
        if (entries.Length == 0)
        {
            // Nothing is capturing — an empty snapshot would erase a good one from before a restart.
            return;
        }

        // Splice onto whatever this hour already had on disk. Without this, the first tick after a
        // mid-hour restart would overwrite the pre-restart history with the new session's
        // counters, which start at zero — losing exactly the coverage this file exists to keep.
        var previous = await ReadSummaryFileAsync(hourKey, cancellationToken).ConfigureAwait(false);
        var merged = entries
            .Select(entry => entry with
            {
                Checkpoints = WindowCheckpointMerger.Merge(
                    FindCheckpoints(previous, entry.SourceId), entry.Checkpoints),
            })
            .ToArray();

        await WriteSummaryFileAsync(hourKey, new CaptureSummarySnapshot(hourKey, now.ToString("o"), merged), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CaptureSummarySnapshot?> ReadSummaryFileAsync(string hourKey, CancellationToken cancellationToken)
    {
        try
        {
            return await evidenceFileStore
                .ReadJsonAsync<CaptureSummarySnapshot>($"monitoringArtifacts/capture-summary-{hourKey}.json", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[CaptureSummaryArchiveWorker] Could not read the existing summary for hour {HourKey}.", hourKey);
            return null;
        }
    }

    private static IReadOnlyList<WindowCheckpoint>? FindCheckpoints(CaptureSummarySnapshot? snapshot, string sourceId) =>
        snapshot?.Sources
            .FirstOrDefault(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            ?.Checkpoints;

    private async Task ArchiveClosedHourAsync(string hourKey, CancellationToken cancellationToken)
    {
        var snapshot = monitoringArtifactRepository.TryPeekHour(hourKey)
            ?? await evidenceFileStore
                .ReadJsonAsync<CaptureSummarySnapshot>($"monitoringArtifacts/capture-summary-{hourKey}.json", cancellationToken)
                .ConfigureAwait(false);

        if (snapshot is null)
        {
            logger.LogWarning("[CaptureSummaryArchiveWorker] No coverage evidence for hour {HourKey} — nothing archived.", hourKey);
            return;
        }

        await summaryStore.UpsertHourAsync(hourKey, snapshot, isClosed: true, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "[CaptureSummaryArchiveWorker] Archived hour {HourKey} ({SourceCount} sources).", hourKey, snapshot.Sources.Length);
    }

    private async Task WriteSummaryFileAsync(string hourKey, CaptureSummarySnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await evidenceFileStore
                .WriteJsonAsync($"monitoringArtifacts/capture-summary-{hourKey}.json", snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[CaptureSummaryArchiveWorker] Could not snapshot the in-progress hour {HourKey}.", hourKey);
        }
    }

    private SourceCaptureSummary[] BuildLiveEntries(DateTimeOffset now, DateTimeOffset currentWindowStart)
    {
        var entries = new List<SourceCaptureSummary>();
        foreach (var sourceId in liveCaptureProgressReader.ActiveSourceIds)
        {
            // A session can end between listing the ids and reading its progress.
            var progress = liveCaptureProgressReader.TryGetLiveProgress(sourceId);

            // Skip a session still reporting an earlier window — it stalled before rotating, and
            // filing its frozen position under this hour would archive coverage that never
            // happened. Its own window was already archived when that hour closed.
            if (progress is not null && progress.WindowStartedAt == currentWindowStart)
            {
                entries.Add(ToSummary(sourceId, progress, now));
            }
        }

        entries.Sort((left, right) => string.Compare(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase));
        return entries.ToArray();
    }

    private static SourceCaptureSummary ToSummary(string sourceId, LiveCaptureProgress progress, DateTimeOffset now)
    {
        var last = progress.Checkpoints[^1];
        var capturedSeconds = (int)Math.Min(Math.Round(last.CapturedSeconds), WindowSeconds);
        var silenceSeconds = (int)Math.Min(Math.Round(last.SilenceFilledSeconds), WindowSeconds);

        // Coverage is measured against how far the recording reached, not against the whole hour:
        // an hour still in progress is not missing the part that hasn't been recorded yet.
        var coveragePercent = progress.RecordedSeconds <= 0
            ? 0
            : Math.Round(capturedSeconds / progress.RecordedSeconds * 100, 2);

        return new SourceCaptureSummary(
            sourceId,
            OpusFile: null,
            capturedSeconds,
            silenceSeconds,
            coveragePercent,
            Status: "live",
            LastCaptureAt: now.ToString("o"),
            progress.Checkpoints);
    }
}
