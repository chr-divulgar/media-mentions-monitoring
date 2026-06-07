using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using System.Collections.Concurrent;
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
            new { sourceId = "source-a", platform = "radio-platform", media = "radio", streamUrl = "https://example.com/radio" },
            new { sourceId = "source-b", platform = "portal-platform", media = "internet", streamUrl = "https://example.com/portal" }
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
                new SuccessfulAudioCapturePlugin(),
                repository,
                1,
                NullCaptureAttemptObserver.Instance);

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
    public async Task ExecuteAsync_should_persist_capture_artifacts_when_capture_succeeds()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(new[]
        {
            new { sourceId = "source-a", platform = "radio", media = "radio", streamUrl = "https://example.com/live" }
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
                new SuccessfulAudioCapturePlugin(),
                repository,
                1,
                NullCaptureAttemptObserver.Instance);

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

    [Fact]
    public async Task ExecuteAsync_should_process_sources_in_parallel_up_to_the_configured_limit()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(new[]
        {
            new { sourceId = "source-a", platform = "radio-a", media = "radio", streamUrl = "https://example.com/a" },
            new { sourceId = "source-b", platform = "radio-b", media = "radio", streamUrl = "https://example.com/b" }
        }));

        var repository = new InMemoryMonitoringArtifactRepository();
        var gate = new ParallelCaptureGate();

        try
        {
            var useCase = new ContinuousCaptureUseCase(
                new StaticCaptureSourceProvider(new OperationsWorkerOptions
                {
                    CaptureSourcesFilePath = tempFilePath,
                    EnableCanaryMode = false,
                    ContinuousMediaAllowList = "radio",
                    CaptureMaxDegreeOfParallelism = 2
                }),
                new StaticPluginResolver(),
                gate,
                repository,
                2,
                NullCaptureAttemptObserver.Instance);

            var execution = useCase.ExecuteAsync();

            var bothStarted = await Task.WhenAny(gate.BothStarted, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(gate.BothStarted, bothStarted);

            gate.Release();

            var result = await execution;
            var artifacts = await repository.ListByTenantAsync("global-ingestion");

            Assert.Equal(2, result.Attempts);
            Assert.Equal(2, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Equal(2, gate.MaxConcurrentObserved);
            Assert.Equal(2, artifacts.Count);
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
                wavWindowDuration: TimeSpan.FromSeconds(5),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1)));
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
                wavWindowDuration: TimeSpan.FromSeconds(5),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1)));
        }
    }

    private sealed class SuccessfulAudioCapturePlugin : IAudioCapturePlugin
    {
        public Task<AudioCaptureExecutionResult> CaptureAsync(
            MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
            PluginExecutionPlan plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AudioCaptureExecutionResult(true, "out.opus"));
        }
    }

    private sealed class ParallelCaptureGate : IAudioCapturePlugin
    {
        private int inFlight;
        private int maxConcurrentObserved;
        private readonly TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BothStarted => bothStarted.Task;

        public int MaxConcurrentObserved => Volatile.Read(ref maxConcurrentObserved);

        public void Release()
        {
            release.TrySetResult();
        }

        public async Task<AudioCaptureExecutionResult> CaptureAsync(
            MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
            PluginExecutionPlan plan,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref inFlight);
            UpdateMaxConcurrentObserved(concurrent);

            if (concurrent >= 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref inFlight);

            return new AudioCaptureExecutionResult(true, $"{source.SourceId}.opus");
        }

        private void UpdateMaxConcurrentObserved(int concurrent)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxConcurrentObserved);
                if (concurrent <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref maxConcurrentObserved, concurrent, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class InMemoryMonitoringArtifactRepository : IMonitoringArtifactRepository
    {
        private readonly ConcurrentDictionary<string, MonitoringArtifact> artifacts = new(StringComparer.Ordinal);

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

