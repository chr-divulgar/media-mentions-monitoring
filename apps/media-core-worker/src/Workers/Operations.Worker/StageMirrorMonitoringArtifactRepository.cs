using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StageMirrorMonitoringArtifactRepository : IMonitoringArtifactRepository
{
    private readonly InMemoryMonitoringArtifactRepository inMemoryRepository;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly IReadOnlyList<IMonitoringArtifactDatabaseRepository> databaseRepositories;

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

        var relativePath = $"monitoringArtifacts/{Uri.EscapeDataString(artifact.Id)}.json";

        try
        {
            await evidenceFileStore.WriteJsonAsync(relativePath, artifact, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Evidence write errors should not stop worker cycle; in-memory store remains source for pipeline continuity.
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
}