using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.BuildingBlocks.Application;

public interface IMonitoringArtifactRepository
{
    Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default);

    Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default);
}