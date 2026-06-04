using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FunctionalParityUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_compare_by_collection_and_compute_overall_parity()
    {
        var repository = new InMemoryMonitoringArtifactRepository();
        await repository.UpsertAsync(new MonitoringArtifact("capture-1", "global-ingestion", "source-1", "capture", "{}", new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero)));
        await repository.UpsertAsync(new MonitoringArtifact("capture-2", "global-ingestion", "source-1", "capture", "{}", new DateTimeOffset(2026, 6, 3, 11, 1, 0, TimeSpan.Zero)));
        await repository.UpsertAsync(new MonitoringArtifact("segment-1", "global-ingestion", "source-1", "segment", "{}", new DateTimeOffset(2026, 6, 3, 11, 2, 0, TimeSpan.Zero)));

        var legacyProvider = new StaticLegacySnapshotProvider(new[]
        {
            new LegacyCollectionSnapshot("capture", 2),
            new LegacyCollectionSnapshot("segment", 2)
        });

        var useCase = new FunctionalParityUseCase(
            repository,
            legacyProvider,
            new FunctionalParityOptions
            {
                MinimumParityPercent = 70
            },
            utcNow: () => new DateTimeOffset(2026, 6, 3, 11, 3, 0, TimeSpan.Zero));

        var report = await useCase.ExecuteAsync();

        Assert.Equal(75, report.OverallParityPercent);
        Assert.True(report.MeetsThreshold);
        Assert.Equal(2, report.Collections.Count);
        Assert.Contains(report.Collections, item => item.Collection == "capture" && item.ParityPercent == 100);
        Assert.Contains(report.Collections, item => item.Collection == "segment" && item.ParityPercent == 50);
    }

    private sealed class StaticLegacySnapshotProvider : ILegacySnapshotProvider
    {
        private readonly IReadOnlyList<LegacyCollectionSnapshot> snapshots;

        public StaticLegacySnapshotProvider(IReadOnlyList<LegacyCollectionSnapshot> snapshots)
        {
            this.snapshots = snapshots;
        }

        public Task<IReadOnlyList<LegacyCollectionSnapshot>> GetCollectionSnapshotsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshots);
        }
    }

    private sealed class InMemoryMonitoringArtifactRepository : IMonitoringArtifactRepository
    {
        private readonly Dictionary<string, MonitoringArtifact> artifacts = new(StringComparer.Ordinal);

        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            artifacts[artifact.Id] = artifact;
            return Task.CompletedTask;
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            artifacts.TryGetValue(id, out var artifact);
            return Task.FromResult(artifact);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            var tenantArtifacts = artifacts.Values
                .Where(artifact => string.Equals(artifact.TenantId, tenantId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(tenantArtifacts);
        }
    }
}