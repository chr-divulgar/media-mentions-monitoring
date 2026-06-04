using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StageMirrorMonitoringArtifactRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_should_keep_local_evidence_when_no_db_sink_is_configured()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var artifact = CreateArtifact("artifact-no-db");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_delete_local_evidence_after_successful_db_persist()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var dbSink = new SuccessfulDatabaseRepository();
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                new[] { dbSink });

            var artifact = CreateArtifact("artifact-db-ok");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.False(File.Exists(filePath));
            Assert.Equal(1, dbSink.UpsertCalls);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_keep_local_evidence_when_db_persist_fails()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                new[] { new FailingDatabaseRepository() });

            var artifact = CreateArtifact("artifact-db-fail");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static MonitoringArtifact CreateArtifact(string id)
    {
        return new MonitoringArtifact(
            id: id,
            tenantId: "global-ingestion",
            source: "unit-test",
            kind: "capture",
            payloadJson: "{}",
            capturedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string BuildEvidencePath(string root, string artifactId)
    {
        var escapedId = Uri.EscapeDataString(artifactId);
        return Path.Combine(root, "monitoringArtifacts", $"{escapedId}.json");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stage-mirror-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SuccessfulDatabaseRepository : IMonitoringArtifactDatabaseRepository
    {
        public int UpsertCalls { get; private set; }

        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            return Task.CompletedTask;
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MonitoringArtifact?>(null);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(Array.Empty<MonitoringArtifact>());
        }
    }

    private sealed class FailingDatabaseRepository : IMonitoringArtifactDatabaseRepository
    {
        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("DB unavailable");
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MonitoringArtifact?>(null);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(Array.Empty<MonitoringArtifact>());
        }
    }
}
