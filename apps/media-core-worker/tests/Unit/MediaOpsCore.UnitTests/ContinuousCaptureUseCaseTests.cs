using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using System.Text.Json;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ContinuousCaptureUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_skip_sources_without_plugin_profile()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(new[]
        {
            new
            {
                sourceId = "source-a",
                platform = "radio-platform",
                media = "radio",
                streamUrl = "https://example.com/radio"
            },
            new
            {
                sourceId = "source-b",
                platform = "portal-platform",
                media = "internet",
                streamUrl = "https://example.com/portal"
            }
        }));

        var repository = new InMemoryMonitoringArtifactRepository();
        try
        {
            var useCase = new ContinuousCaptureUseCase(
                new StaticCaptureSourceProvider(new OperationsWorkerOptions
                {
                    CaptureSourcesFilePath = tempFilePath,
                    EnableCanaryMode = false,
                    ContinuousMediaAllowList = ""
                }),
                new RadioOnlyPluginResolver(),
                new SuccessfulProcessRunner(),
                repository);

            var result = await useCase.ExecuteAsync();
            var artifacts = await repository.ListByTenantAsync("global-ingestion");

            Assert.Equal(2, result.Attempts);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Single(artifacts);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_should_persist_capture_artifacts_when_process_succeeds()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(new[]
        {
            new
            {
                sourceId = "source-a",
                platform = "radio",
                media = "radio",
                streamUrl = "https://example.com/live"
            }
        }));

        var repository = new InMemoryMonitoringArtifactRepository();
        try
        {
            var useCase = new ContinuousCaptureUseCase(
                new StaticCaptureSourceProvider(new OperationsWorkerOptions
                {
                    CaptureSourcesFilePath = tempFilePath
                }),
                new StaticPluginResolver(),
                new SuccessfulProcessRunner(),
                repository);

            var result = await useCase.ExecuteAsync();
            var artifacts = await repository.ListByTenantAsync("global-ingestion");

            Assert.Equal(1, result.Attempts);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Single(artifacts);
            Assert.Equal("capture", artifacts[0].Kind);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private sealed class StaticPluginResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(
            MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
            IngestionMode ingestionMode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PluginExecutionPlan(
                pluginId: "test-plugin",
                toolExecutable: "cmd.exe",
                toolArgumentsTemplate: "/c echo capture {url}",
                commandTimeout: TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class RadioOnlyPluginResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(
            MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
            IngestionMode ingestionMode,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(source.Media, "radio", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"No plugin profile configured for media '{source.Media}', platform '{source.Platform}', mode '{ingestionMode}'.");
            }

            return Task.FromResult(new PluginExecutionPlan(
                pluginId: "radio-plugin",
                toolExecutable: "cmd.exe",
                toolArgumentsTemplate: "/c echo capture {url}",
                commandTimeout: TimeSpan.FromSeconds(5)));
        }
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