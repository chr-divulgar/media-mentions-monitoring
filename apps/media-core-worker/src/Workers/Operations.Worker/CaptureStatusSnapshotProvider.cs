using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

public interface ICaptureStatusSnapshotProvider
{
    Task<CaptureStatusResponse> GetSnapshotAsync(DateOnly date, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the per-source, per-hour capture status for one day. Closed hours come from
/// StageMirrorMonitoringArtifactRepository's evidence — peeked from its in-memory bucket first
/// (WriteSummaryFileAsync only flushes an hour to disk once the NEXT hour also closes, so peeking
/// avoids that up-to-an-hour lag), falling back to disk for older hours already flushed and
/// evicted. The in-progress hour has no evidence anywhere yet (a source only emits its capture
/// artifact at rotation) — its status instead comes live from ILiveCaptureProgressReader, which
/// reads each active session's periodic window checkpoints directly.
/// </summary>
public sealed class CaptureStatusSnapshotProvider : ICaptureStatusSnapshotProvider
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const double WindowSeconds = 3600.0;

    private readonly ICaptureSourceRepository captureSourceRepository;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly StageMirrorMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ILiveCaptureProgressReader liveCaptureProgressReader;

    public CaptureStatusSnapshotProvider(
        ICaptureSourceRepository captureSourceRepository,
        IEvidenceFileStore evidenceFileStore,
        StageMirrorMonitoringArtifactRepository monitoringArtifactRepository,
        ILiveCaptureProgressReader liveCaptureProgressReader)
    {
        this.captureSourceRepository = captureSourceRepository;
        this.evidenceFileStore = evidenceFileStore;
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.liveCaptureProgressReader = liveCaptureProgressReader;
    }

    public async Task<CaptureStatusResponse> GetSnapshotAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var nowBogota = DateTimeOffset.UtcNow.ToOffset(BogotaOffset);
        var isToday = date == DateOnly.FromDateTime(nowBogota.DateTime);
        var maxHour = isToday ? nowBogota.Hour : 23;
        var liveHour = isToday ? nowBogota.Hour : -1;

        // Includes the in-progress hour: CaptureSummaryArchiveWorker keeps its coverage on disk
        // as it goes, which is the only record of what was captured before a mid-hour restart —
        // the session running now knows nothing about it.
        var hourSnapshots = new CaptureSummarySnapshot?[maxHour + 1];
        for (var hour = 0; hour <= maxHour; hour++)
        {
            var hourKey = $"{date:yyyy-MM-dd}_{hour:00}";
            hourSnapshots[hour] = monitoringArtifactRepository.TryPeekHour(hourKey)
                ?? await evidenceFileStore
                    .ReadJsonAsync<CaptureSummarySnapshot>($"monitoringArtifacts/capture-summary-{hourKey}.json", cancellationToken)
                    .ConfigureAwait(false);
        }

        var sources = await captureSourceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);

        var sourceEntries = sources
            .Select(source =>
            {
                // Which hour is still being filled is the session's call, not the clock's. A source
                // running behind the wall clock keeps recording the previous hour for minutes after
                // it ends, and closing it on the clock's say-so drew those last minutes as
                // no-session — the evidence for them only lands with the rotation artifact.
                var inProgressHour = ResolveRecordingHour(source.SourceId, date) ?? liveHour;

                return new CaptureSourceStatusEntry
                {
                    SourceId = source.SourceId,
                    Platform = source.Platform,
                    Media = source.Media,
                    IsExcluded = source.IsExcluded,
                    Hours = Enumerable.Range(0, maxHour + 1)
                        .Select(hour => hour == inProgressHour
                            ? BuildLiveHourEntry(hour, date, source.SourceId, hourSnapshots[hour])
                            : BuildClosedHourEntry(hour, hourSnapshots[hour], source.SourceId))
                        .ToArray(),
                };
            })
            .ToArray();

        return new CaptureStatusResponse
        {
            Date = date.ToString("yyyy-MM-dd"),
            Sources = sourceEntries,
        };
    }

    private CaptureHourStatusEntry BuildLiveHourEntry(
        int hour, DateOnly date, string sourceId, CaptureSummarySnapshot? archivedHour)
    {
        var archived = archivedHour?.Sources
            .FirstOrDefault(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            ?.Checkpoints;
        var progress = liveCaptureProgressReader.TryGetLiveProgress(sourceId);

        // A session only leaves a window once its own audio timeline crosses the boundary, so a
        // stalled one keeps reporting the window it froze in. Drawing that against this hour is
        // what made a source look like it had already recorded time that hasn't happened yet.
        if (progress is not null && !BelongsToHour(progress.WindowStartedAt, date, hour))
        {
            progress = null;
        }

        if (progress is null)
        {
            // Nothing is capturing this source right now, but the hour may still have been
            // recorded before the worker stopped — show that rather than claiming no data.
            return archived is { Count: > 0 }
                ? new CaptureHourStatusEntry
                {
                    Hour = hour,
                    Status = "live",
                    Segments = ToSegmentEntries(CaptureSegmentBuilder.Build(archived, archived[^1].ElapsedSeconds)),
                }
                : new CaptureHourStatusEntry { Hour = hour, Status = "no-data" };
        }

        var checkpoints = WindowCheckpointMerger.Merge(archived, progress.Checkpoints);

        return new CaptureHourStatusEntry
        {
            Hour = hour,
            Status = "live",
            Segments = ToSegmentEntries(
                CaptureSegmentBuilder.Build(checkpoints, progress.RecordedSeconds)),
        };
    }

    private static CaptureHourStatusEntry BuildClosedHourEntry(int hour, CaptureSummarySnapshot? hourSnapshot, string sourceId)
    {
        var sourceSummary = hourSnapshot?.Sources.FirstOrDefault(
            s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        if (sourceSummary is null)
        {
            return new CaptureHourStatusEntry { Hour = hour, Status = "no-data" };
        }

        // The persisted checkpoints already carry both ends of the window (the session brackets
        // every snapshot), and their offsets are positions in that hour's recording, so they map
        // straight onto the hour without reprojection.
        IReadOnlyList<CaptureSegmentEntry>? segments = null;
        if (sourceSummary.Checkpoints is { Count: > 0 } checkpoints)
        {
            segments = ToSegmentEntries(CaptureSegmentBuilder.Build(checkpoints, WindowSeconds));
        }

        return new CaptureHourStatusEntry
        {
            Hour = hour,
            Status = sourceSummary.Status,
            CoveragePercent = sourceSummary.CoveragePercent,
            Segments = segments,
        };
    }

    // The hour this source's session is still recording into, or null when it isn't recording this
    // date at all. A session only leaves a window once its own audio timeline crosses the boundary,
    // so this trails the wall clock for a source running behind and leads it for one running ahead.
    private int? ResolveRecordingHour(string sourceId, DateOnly date)
    {
        var progress = liveCaptureProgressReader.TryGetLiveProgress(sourceId);
        if (progress is null)
        {
            return null;
        }

        var windowStart = progress.WindowStartedAt.ToOffset(BogotaOffset);
        return DateOnly.FromDateTime(windowStart.DateTime) == date ? windowStart.Hour : null;
    }

    private static bool BelongsToHour(DateTimeOffset windowStart, DateOnly date, int hour)
    {
        var hourStart = new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, BogotaOffset);
        return windowStart >= hourStart && windowStart < hourStart.AddSeconds(WindowSeconds);
    }

    private static IReadOnlyList<CaptureSegmentEntry> ToSegmentEntries(IReadOnlyList<CaptureSegment> segments) =>
        segments
            .Select(s => new CaptureSegmentEntry { StartSeconds = s.StartSeconds, EndSeconds = s.EndSeconds, State = s.State })
            .ToArray();
}
