using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StageMirrorMonitoringArtifactRepository : IMonitoringArtifactRepository
{
    private static readonly JsonSerializerOptions SummarySerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly InMemoryMonitoringArtifactRepository inMemoryRepository;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly IReadOnlyList<IMonitoringArtifactDatabaseRepository> databaseRepositories;

    // Per-hour accumulator: key = "YYYY-MM-DD_HH" (local to source offset), value = mutable summary
    private readonly ConcurrentDictionary<string, HourlySummary> hourlySummaries =
        new(StringComparer.Ordinal);

    // Tracks the last hour key written per source so we can detect hour transitions
    private readonly ConcurrentDictionary<string, string> lastHourKeyBySource =
        new(StringComparer.OrdinalIgnoreCase);

    public StageMirrorMonitoringArtifactRepository(
        InMemoryMonitoringArtifactRepository inMemoryRepository,
        IEvidenceFileStore evidenceFileStore,
        IEnumerable<IMonitoringArtifactDatabaseRepository>? databaseRepositories = null)
    {
        this.inMemoryRepository = inMemoryRepository;
        this.evidenceFileStore = evidenceFileStore;
        this.databaseRepositories = databaseRepositories?.ToArray() ?? Array.Empty<IMonitoringArtifactDatabaseRepository>();
    }

    public async Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
    {
        await inMemoryRepository.UpsertAsync(artifact, cancellationToken).ConfigureAwait(false);

        if (string.Equals(artifact.Kind, "capture", StringComparison.Ordinal))
        {
            await UpsertHourlySummaryAsync(artifact, cancellationToken).ConfigureAwait(false);
        }

        foreach (var databaseRepository in databaseRepositories)
        {
            try
            {
                await databaseRepository.UpsertAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // DB sink failures should not stop worker cycle.
            }
        }
    }

    public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        return inMemoryRepository.GetAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        return inMemoryRepository.ListByTenantAsync(tenantId, cancellationToken);
    }

    private async Task UpsertHourlySummaryAsync(MonitoringArtifact artifact, CancellationToken cancellationToken)
    {
        var currentHourKey = ToHourKey(artifact.CapturedAtUtc);
        var sourceId = string.IsNullOrWhiteSpace(artifact.Source) ? "unknown" : artifact.Source;

        var (succeeded, opusFilePath, capturedSeconds, silenceFilledSeconds, checkpoints) = ParseCapturePayload(artifact.PayloadJson);

        // Accumulate in the current hour's in-memory bucket
        var currentSummary = hourlySummaries.GetOrAdd(currentHourKey, key => new HourlySummary(key));
        currentSummary.Upsert(sourceId, succeeded, opusFilePath, capturedSeconds, silenceFilledSeconds, checkpoints, artifact.CapturedAtUtc);

        // Persist as soon as the evidence exists rather than waiting for the source to cross into
        // the next hour. A source emits one capture artifact per hour (at rotation), so this is a
        // handful of writes per hour, and it is what makes the hour survive a worker restart: the
        // transition-triggered write below never fires on the first rotation of a run (there is no
        // previous hour key yet), so a worker stopped within the hour used to lose it entirely.
        await WriteSummaryFileAsync(currentHourKey, currentSummary, cancellationToken).ConfigureAwait(false);

        // When a source crosses into a new hour, the previous hour is now complete for that source
        // and its bucket can be dropped — it is already on disk.
        if (lastHourKeyBySource.TryGetValue(sourceId, out var prevHourKey) && prevHourKey != currentHourKey)
        {
            if (hourlySummaries.TryGetValue(prevHourKey, out var prevSummary))
            {
                await WriteSummaryFileAsync(prevHourKey, prevSummary, cancellationToken).ConfigureAwait(false);
            }

            hourlySummaries.TryRemove(prevHourKey, out _);
        }

        lastHourKeyBySource[sourceId] = currentHourKey;
    }

    private static (bool Succeeded, string OpusFilePath, double CapturedSeconds, double SilenceFilledSeconds, IReadOnlyList<WindowCheckpoint> Checkpoints) ParseCapturePayload(string payloadJson)
    {
        var succeeded = false;
        var opusFilePath = string.Empty;
        var capturedSeconds = 0.0;
        var silenceFilledSeconds = 0.0;
        var checkpoints = Array.Empty<WindowCheckpoint>();

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    succeeded = prop.Value.GetBoolean();
                else if (prop.Name.Equals("opusFilePath", StringComparison.OrdinalIgnoreCase))
                    opusFilePath = prop.Value.GetString() ?? string.Empty;
                else if (prop.Name.Equals("capturedSeconds", StringComparison.OrdinalIgnoreCase))
                    capturedSeconds = prop.Value.GetDouble();
                else if (prop.Name.Equals("silenceFilledSeconds", StringComparison.OrdinalIgnoreCase))
                    silenceFilledSeconds = prop.Value.GetDouble();
                else if (prop.Name.Equals("checkpoints", StringComparison.OrdinalIgnoreCase))
                    checkpoints = prop.Value.Deserialize<WindowCheckpoint[]>() ?? Array.Empty<WindowCheckpoint>();
            }
        }
        catch { }

        return (succeeded, opusFilePath, capturedSeconds, silenceFilledSeconds, checkpoints);
    }

    private async Task WriteSummaryFileAsync(string hourKey, HourlySummary summary, CancellationToken cancellationToken)
    {
        var snapshot = summary.ToSnapshot();
        var summaryPath = $"monitoringArtifacts/capture-summary-{hourKey}.json";
        try
        {
            // A source that restarted mid-hour emits its closing artifact with only the coverage
            // the surviving session knows about; CaptureSummaryArchiveWorker has the rest on disk.
            // Splice them so closing the hour records what was actually captured across the whole
            // window instead of overwriting the earlier part with a session that started late.
            var existing = await ReadSummaryFileAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                snapshot = snapshot with
                {
                    Sources = snapshot.Sources
                        .Select(source => source with
                        {
                            Checkpoints = WindowCheckpointMerger.Merge(
                                existing.Sources
                                    .FirstOrDefault(previous => string.Equals(previous.SourceId, source.SourceId, StringComparison.OrdinalIgnoreCase))
                                    ?.Checkpoints,
                                source.Checkpoints),
                        })
                        .ToArray(),
                };
            }

            await evidenceFileStore.WriteJsonAsync(summaryPath, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Evidence write errors should not stop worker cycle.
        }
    }

    private async Task<CaptureSummarySnapshot?> ReadSummaryFileAsync(string summaryPath, CancellationToken cancellationToken)
    {
        try
        {
            return await evidenceFileStore
                .ReadJsonAsync<CaptureSummarySnapshot>(summaryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // No readable history for this hour — the incoming snapshot stands on its own.
            return null;
        }
    }

    private static string ToHourKey(DateTimeOffset value) =>
        $"{value:yyyy-MM-dd_HH}";

    // Reads back an hour's evidence straight from the in-memory bucket, saving a disk round-trip
    // for the hours still being accumulated. Returns null if nothing is buffered for that key (the
    // hour hasn't happened yet, or its bucket was already evicted — callers fall back to
    // IEvidenceFileStore, which has held the hour since the moment it closed).
    public CaptureSummarySnapshot? TryPeekHour(string hourKey) =>
        hourlySummaries.TryGetValue(hourKey, out var summary) ? summary.ToSnapshot() : null;

    // Thread-safe per-hour accumulator
    private sealed class HourlySummary(string hourKey)
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<string, SourceEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        public void Upsert(string sourceId, bool succeeded, string opusFilePath, double capturedSeconds, double silenceFilledSeconds, IReadOnlyList<WindowCheckpoint> checkpoints, DateTimeOffset capturedAt)
        {
            lock (syncRoot)
            {
                if (!entries.TryGetValue(sourceId, out var entry))
                {
                    entry = new SourceEntry { SourceId = sourceId };
                    entries[sourceId] = entry;
                }

                entry.CaptureCount++;
                if (succeeded) entry.SucceededCount++;

                if (!string.IsNullOrWhiteSpace(opusFilePath)) entry.OpusFilePath = opusFilePath;
                // Take the maximum seen — values only grow as the session records more audio
                if (capturedSeconds > entry.CapturedSeconds) entry.CapturedSeconds = capturedSeconds;
                if (silenceFilledSeconds > entry.SilenceFilledSeconds) entry.SilenceFilledSeconds = silenceFilledSeconds;
                // A source only emits one "capture" artifact per hour (at rotation), so there is
                // nothing to merge — the latest (only) set of checkpoints wins.
                if (checkpoints.Count > 0) entry.Checkpoints = checkpoints;
                entry.LastCaptureAt = capturedAt;
            }
        }

        private const double RotationWindowSeconds = 3600.0; // 1-hour rotation

        public CaptureSummarySnapshot ToSnapshot()
        {
            lock (syncRoot)
            {
                var sources = entries.Values
                    .OrderBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
                    .Select(e =>
                    {
                        var silenceSec = (int)Math.Round(e.SilenceFilledSeconds, 0);
                        // capturedSeconds comes directly from real encoded audio samples in the
                        // capture plugin — not derived from heartbeat counts or intervals.
                        var capturedSec = (int)Math.Min(Math.Round(e.CapturedSeconds, 0), RotationWindowSeconds);
                        var coveragePct = Math.Round(capturedSec / RotationWindowSeconds * 100.0, 1);

                        return new SourceCaptureSummary(
                            e.SourceId,
                            e.OpusFilePath is not null ? Path.GetFileName(e.OpusFilePath) : null,
                            capturedSec,
                            silenceSec,
                            coveragePct,
                            ResolveStatus(e),
                            e.LastCaptureAt?.ToString("HH:mm:ss zzz"),
                            e.Checkpoints);
                    })
                    .ToArray();

                return new CaptureSummarySnapshot(hourKey, DateTimeOffset.UtcNow.ToString("o"), sources);
            }
        }

        private static string ResolveStatus(SourceEntry e)
        {
            if (e.SucceededCount == 0) return "excluded";

            if (e.Checkpoints.Count == 0)
            {
                // No checkpoint data to derive a timeline from (evidence written before this
                // feature existed) — fall back to the coarse signal only.
                return e.SilenceFilledSeconds > 0 ? "gap-filled" : "ok";
            }

            // Reuse the same segment classification the detailed per-minute view uses, so the
            // coarse hour tile agrees with it instead of judging status from raw silence-filled
            // seconds alone — that conflated "worker never ran this stretch" (no-session) with
            // "worker ran and the stream/gap-fill produced silence" (gap), both surfacing as the
            // same "gap-filled" verdict (or, if silenceFilledSeconds was never recorded, silently
            // as "ok" even when the worker was actually down).
            var checkpoints = e.Checkpoints.Append(new WindowCheckpoint(RotationWindowSeconds, e.CapturedSeconds, e.SilenceFilledSeconds)).ToArray();
            var segments = CaptureSegmentBuilder.Build(checkpoints, RotationWindowSeconds);

            var noSessionSeconds = segments.Where(s => s.State == "no-session").Sum(s => s.EndSeconds - s.StartSeconds);
            if (noSessionSeconds >= RotationWindowSeconds / 2) return "no-session";

            var gapSeconds = segments.Where(s => s.State == "gap").Sum(s => s.EndSeconds - s.StartSeconds);
            if (noSessionSeconds > 0 || gapSeconds > 0) return "gap-filled";

            return "ok";
        }

        private sealed class SourceEntry
        {
            public string SourceId { get; init; } = string.Empty;
            public string? OpusFilePath { get; set; }
            public int CaptureCount { get; set; }
            public int SucceededCount { get; set; }
            public double CapturedSeconds { get; set; }
            public double SilenceFilledSeconds { get; set; }
            public DateTimeOffset? LastCaptureAt { get; set; }
            public IReadOnlyList<WindowCheckpoint> Checkpoints { get; set; } = Array.Empty<WindowCheckpoint>();
        }
    }
}

// Shape written to monitoringArtifacts/capture-summary-{yyyy-MM-dd}_{HH}.json (and returned live
// for the in-progress hour via PeekCurrentHourSnapshot) — shared by the write path above and by
// CaptureStatusSnapshotProvider's read-back of already-closed hours.
public sealed record CaptureSummarySnapshot(string Window, string ClosedAt, SourceCaptureSummary[] Sources);

public sealed record SourceCaptureSummary(
    string SourceId,
    string? OpusFile,
    int CapturedSeconds,
    int SilenceSeconds,
    double CoveragePercent,
    string Status,
    string? LastCaptureAt,
    IReadOnlyList<WindowCheckpoint>? Checkpoints = null);

// A periodic (every few minutes) sample of a capture session's running window counters — cheap to
// take since it only reads already-thread-safe fields (CaptureSession.CapturedThisWindowSeconds /
// SilenceFilledThisWindowSeconds), never touches the FFmpeg read loop. The delta between two
// consecutive checkpoints tells whether that slice of the hour was actually recorded or not,
// without needing to track exact gap start/end timestamps.
/// <summary>
/// One periodic sample of a capture window's counters. ElapsedSeconds is a position in the
/// recording (seconds from the window start, silence fill included), read from the encoder's
/// sample cursor rather than a clock — see LiveCaptureProgress.
/// </summary>
public sealed record WindowCheckpoint(double ElapsedSeconds, double CapturedSeconds, double SilenceFilledSeconds);