using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ContinuousCaptureUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_persist_capture_artifacts_when_process_succeeds()
    {
        var repository = new InMemoryMonitoringArtifactRepository();
        var useCase = new ContinuousCaptureUseCase(
            new StaticCaptureSourceProvider(new OperationsWorkerOptions
            {
                IngestionScopeId = "global-ingestion",
                CaptureSourceId = "source-a",
                CapturePlatform = "radio",
                CaptureMedia = "news",
                CaptureStreamUrl = "https://example.com/live"
            }),
            new SuccessfulProcessRunner(),
            repository,
            new ContinuousCaptureOptions
            {
                ToolExecutable = "cmd.exe",
                ToolArgumentsTemplate = "/c echo capture {url}",
                CommandTimeout = TimeSpan.FromSeconds(5)
            });

        var result = await useCase.ExecuteAsync();
    var artifacts = await repository.ListByTenantAsync("global-ingestion");

        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Single(artifacts);
        Assert.Equal("capture", artifacts[0].Kind);
    }

    private sealed class SuccessfulProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult(0, "ok", string.Empty, false));
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
            var tenantArtifacts = artifacts.Values.Where(artifact => artifact.TenantId == tenantId).ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(tenantArtifacts);
        }
    }
}