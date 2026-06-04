using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class InMemoryMonitoringArtifactRepository : IMonitoringArtifactRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, MonitoringArtifact> artifacts = new(StringComparer.Ordinal);

    public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            artifacts[artifact.Id] = artifact;
        }

        return Task.CompletedTask;
    }

    public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            artifacts.TryGetValue(id, out var artifact);
            return Task.FromResult(artifact);
        }
    }

    public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            var tenantArtifacts = artifacts.Values
                .Where(artifact => string.Equals(artifact.TenantId, tenantId, StringComparison.Ordinal))
                .OrderBy(artifact => artifact.CapturedAtUtc)
                .ToArray();

            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(tenantArtifacts);
        }
    }
}