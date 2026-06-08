using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using System.Collections.Concurrent;

namespace MediaOpsCore.Workers.Operations;

public sealed class StageMirrorMonitoringArtifactRepository : IMonitoringArtifactRepository
{
    private readonly InMemoryMonitoringArtifactRepository inMemoryRepository;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly IReadOnlyList<IMonitoringArtifactDatabaseRepository> databaseRepositories;
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastPersistedCaptureHourBySource =
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

        var shouldPersistLocalEvidence = ShouldPersistLocalEvidence(artifact);
        var relativePath = shouldPersistLocalEvidence
            ? $"monitoringArtifacts/{Uri.EscapeDataString(artifact.Id)}.json"
            : null;

        if (relativePath is not null)
        {
            try
            {
                await evidenceFileStore.WriteJsonAsync(relativePath, artifact, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Evidence write errors should not stop worker cycle; in-memory store remains source for pipeline continuity.
            }
        }

        var wasPersistedInDatabase = false;
        foreach (var databaseRepository in databaseRepositories)
        {
            try
            {
                await databaseRepository.UpsertAsync(artifact, cancellationToken).ConfigureAwait(false);
                wasPersistedInDatabase = true;
            }
            catch
            {
                // DB sink failures should not stop worker cycle; local evidence remains as fallback.
            }
        }

        if (!wasPersistedInDatabase)
        {
            return;
        }

        if (relativePath is null)
        {
            return;
        }

        try
        {
            await evidenceFileStore.DeleteAsync(relativePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Delete failures should not stop worker cycle.
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

    private bool ShouldPersistLocalEvidence(MonitoringArtifact artifact)
    {
        if (!string.Equals(artifact.Kind, "capture", StringComparison.Ordinal))
        {
            return false;
        }

        var sourceKey = string.IsNullOrWhiteSpace(artifact.Source)
            ? "unknown-source"
            : artifact.Source;

        var captureHour = TruncateToSourceHour(artifact.CapturedAtUtc);

        while (true)
        {
            if (!lastPersistedCaptureHourBySource.TryGetValue(sourceKey, out var lastPersistedHour))
            {
                if (lastPersistedCaptureHourBySource.TryAdd(sourceKey, captureHour))
                {
                    return true;
                }

                continue;
            }

            if (captureHour <= lastPersistedHour)
            {
                return false;
            }

            if (lastPersistedCaptureHourBySource.TryUpdate(sourceKey, captureHour, lastPersistedHour))
            {
                return true;
            }
        }
    }

    private static DateTimeOffset TruncateToSourceHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);
    }
}