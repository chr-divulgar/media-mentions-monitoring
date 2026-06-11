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

        // Parse payload with case-insensitive options — the payload is serialized in PascalCase
        // by the default JsonSerializer but we cannot rely on any specific casing.
        var (succeeded, opusFilePath, silenceFilledSeconds) = ParseCapturePayload(artifact.PayloadJson);

        // Accumulate in the current hour's in-memory bucket
        hourlySummaries
            .GetOrAdd(currentHourKey, key => new HourlySummary(key))
            .Upsert(sourceId, succeeded, opusFilePath, silenceFilledSeconds, artifact.CapturedAtUtc);

        // When a source crosses into a new hour, the previous hour is now complete for that source.
        // Write the summary for the previous hour (may be called multiple times as sources transition
        // at slightly different moments — each call updates the same file, last write wins).
        if (lastHourKeyBySource.TryGetValue(sourceId, out var prevHourKey) && prevHourKey != currentHourKey)
        {
            if (hourlySummaries.TryGetValue(prevHourKey, out var prevSummary))
            {
                await WriteSummaryFileAsync(prevHourKey, prevSummary, cancellationToken).ConfigureAwait(false);
            }

            // Remove old bucket once written — sources that transition later will re-create it if needed
            hourlySummaries.TryRemove(prevHourKey, out _);
        }

        lastHourKeyBySource[sourceId] = currentHourKey;
    }

    private static (bool Succeeded, string OpusFilePath, double SilenceFilledSeconds) ParseCapturePayload(string payloadJson)
    {
        var succeeded = false;
        var opusFilePath = string.Empty;
        var silenceFilledSeconds = 0.0;

        try
        {
            // Case-insensitive options to handle PascalCase/camelCase inconsistencies
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    succeeded = prop.Value.GetBoolean();
                else if (prop.Name.Equals("opusFilePath", StringComparison.OrdinalIgnoreCase))
                    opusFilePath = prop.Value.GetString() ?? string.Empty;
                else if (prop.Name.Equals("silenceFilledSeconds", StringComparison.OrdinalIgnoreCase))
                    silenceFilledSeconds = prop.Value.GetDouble();
            }
        }
        catch { }

        return (succeeded, opusFilePath, silenceFilledSeconds);
    }

    private async Task WriteSummaryFileAsync(string hourKey, HourlySummary summary, CancellationToken cancellationToken)
    {
        var summaryPath = $"monitoringArtifacts/capture-summary-{hourKey}.json";
        try
        {
            await evidenceFileStore.WriteJsonAsync(summaryPath, summary.ToSnapshot(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Evidence write errors should not stop worker cycle.
        }
    }

    private static string ToHourKey(DateTimeOffset value) =>
        $"{value:yyyy-MM-dd_HH}";

    // Thread-safe per-hour accumulator
    private sealed class HourlySummary(string hourKey)
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<string, SourceEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        public void Upsert(string sourceId, bool succeeded, string opusFilePath, double silenceFilledSeconds, DateTimeOffset capturedAt)
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
                // Take the maximum seen — silence only grows within a window
                if (silenceFilledSeconds > entry.SilenceFilledSeconds)
                    entry.SilenceFilledSeconds = silenceFilledSeconds;
                entry.LastCaptureAt = capturedAt;
            }
        }

        public object ToSnapshot()
        {
            lock (syncRoot)
            {
                var sources = entries.Values
                    .OrderBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
                    .Select(e =>
                    {
                        var silenceSec = Math.Round(e.SilenceFilledSeconds, 0);
                        // CaptureCount × 1-minute heartbeat ≈ captured minutes.
                        // Each heartbeat corresponds to ~1 min of active recording.
                        var capturedSec = (double)e.SucceededCount * 60;
                        var totalSec = capturedSec + silenceSec;
                        var coveragePct = totalSec > 0 ? Math.Round(totalSec / 3600.0 * 100, 1) : 0.0;

                        return new
                        {
                            e.SourceId,
                            OpusFile = e.OpusFilePath is not null ? Path.GetFileName(e.OpusFilePath) : null,
                            CapturedSeconds = (int)capturedSec,
                            SilenceSeconds = (int)silenceSec,
                            CoveragePercent = coveragePct,
                            Status = ResolveStatus(e),
                            LastCaptureAt = e.LastCaptureAt?.ToString("HH:mm:ss zzz")
                        };
                    })
                    .ToArray();

                return new { Window = hourKey, ClosedAt = DateTimeOffset.UtcNow.ToString("o"), Sources = sources };
            }
        }

        private static string ResolveStatus(SourceEntry e)
        {
            if (e.SucceededCount == 0) return "excluded";
            if (e.SilenceFilledSeconds > 0) return "gap-filled";
            return "ok";
        }

        private sealed class SourceEntry
        {
            public string SourceId { get; init; } = string.Empty;
            public string? OpusFilePath { get; set; }
            public int CaptureCount { get; set; }
            public int SucceededCount { get; set; }
            public double SilenceFilledSeconds { get; set; }
            public DateTimeOffset? LastCaptureAt { get; set; }
        }
    }
}