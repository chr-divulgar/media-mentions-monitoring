using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Modules.Segmentation.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class IncrementalSegmentationUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_generate_segments_only_for_new_capture_artifacts()
    {
        var repository = new InMemoryMonitoringArtifactRepository();
        var cursorRepository = new InMemorySegmentCursorRepository();

        await repository.UpsertAsync(new MonitoringArtifact(
            "capture-1",
            "global-ingestion",
            "source-a",
            "capture",
            "{}",
            new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero)));

        await repository.UpsertAsync(new MonitoringArtifact(
            "capture-2",
            "global-ingestion",
            "source-a",
            "capture",
            "{}",
            new DateTimeOffset(2026, 6, 3, 11, 5, 0, TimeSpan.Zero)));

        await cursorRepository.SaveLastProcessedAtAsync(
            "global-ingestion",
            new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero));

        var useCase = new IncrementalSegmentationUseCase(
            repository,
            cursorRepository,
            new IncrementalSegmentationOptions
            {
                SegmentDurationSeconds = 30
            },
            utcNow: () => new DateTimeOffset(2026, 6, 3, 11, 6, 0, TimeSpan.Zero));

        var result = await useCase.ExecuteAsync();
        var artifacts = await repository.ListByTenantAsync("global-ingestion");

        Assert.Equal(2, result.CapturesScanned);
        Assert.Equal(1, result.SegmentsGenerated);
        Assert.Single(artifacts, artifact => artifact.Kind == "segment");
        Assert.Equal(60, result.PipelineLagSeconds);
    }

    private sealed class InMemorySegmentCursorRepository : ISegmentCursorRepository
    {
        private readonly Dictionary<string, DateTimeOffset> cursors = new(StringComparer.Ordinal);

        public Task<DateTimeOffset?> GetLastProcessedAtAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(cursors.TryGetValue(tenantId, out var cursor) ? cursor : (DateTimeOffset?)null);
        }

        public Task SaveLastProcessedAtAsync(string tenantId, DateTimeOffset lastProcessedAtUtc, CancellationToken cancellationToken = default)
        {
            cursors[tenantId] = lastProcessedAtUtc;
            return Task.CompletedTask;
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
                .Where(artifact => artifact.TenantId == tenantId)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(tenantArtifacts);
        }
    }
}