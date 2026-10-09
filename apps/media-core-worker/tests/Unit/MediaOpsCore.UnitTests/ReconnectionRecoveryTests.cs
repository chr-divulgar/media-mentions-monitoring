using System.Collections.Concurrent;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// What the worker does when the internet connection goes away and returns (no restart involved).
public sealed class ReconnectionRecoveryTests
{
    private sealed class Monitor : IConnectivityMonitor
    {
        public bool IsOnline { get; set; } = true;

        public Task WaitUntilOnlineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event Action? CameOnline;

        public void Raise() => CameOnline?.Invoke();
    }

    private sealed class Incidents : ISourceIncidentRecorder
    {
        private readonly ConcurrentQueue<(string SourceId, string Kind, string? Reason)> records = new();

        public IReadOnlyList<(string SourceId, string Kind, string? Reason)> Records => records.ToArray();

        public void Record(string sourceId, string kind, string? reason = null) => records.Enqueue((sourceId, kind, reason));

        public async Task<bool> WaitForAsync(string kind)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (records.Any(record => record.Kind == kind)) return true;
                await Task.Delay(50);
            }

            return false;
        }
    }

    private sealed class Repository(CaptureSource source) : ICaptureSourceRepository
    {
        public Dictionary<string, bool> Exclusions { get; } = [];

        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CaptureSource> sources = [source];
            return Task.FromResult(sources);
        }

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
        {
            Exclusions[sourceId] = excluded;
            return Task.FromResult(true);
        }
    }

    private sealed class WorkingStream : IStartupStreamValidator
    {
        public Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StartupStreamValidationResult(Succeeded: true, ErrorMessage: null));
    }

    private sealed class NoYouTube : ILiveStreamUrlResolver
    {
        public bool CanResolve(CaptureSource source) => false;

        public Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoAlert : IYouTubeCookiesAlertService
    {
        public string AlertFilePath => "alert.flag";
        public bool AlertExists() => false;
        public void WriteAlert(string sourceId, string errorMessage) { }
        public void ClearAlert() { }
    }

    private sealed class NoPlugin : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(CaptureSource source, IngestionMode ingestionMode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("TriggerCaptureAsync runs fire-and-forget and swallows exceptions.");
    }

    [Fact]
    public async Task When_the_connection_returns_every_excluded_source_is_recovered_at_once()
    {
        var excluded = new CaptureSource("rcn", tenantId: "t", platform: "generic", media: "radio", streamUrl: "https://example.com/radio", isExcluded: true);
        var repository = new Repository(excluded);
        var monitor = new Monitor();
        var incidents = new Incidents();
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository, monitor);
        var service = new SourceAvailabilityReconciliationService(
            provider, new WorkingStream(), new NoPlugin(), new ServiceCollection().BuildServiceProvider(),
            new NoYouTube(), new NoAlert(), NullLogger<SourceAvailabilityReconciliationService>.Instance, incidents, monitor);

        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise();

            Assert.True(await incidents.WaitForAsync(IncidentKinds.Recovered));
            Assert.Contains(incidents.Records, record => record.Kind == IncidentKinds.Recovered && record.Reason == "Recuperada al volver la conexión");
            Assert.False(repository.Exclusions["rcn"]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task No_source_is_retried_or_marked_failed_while_there_is_no_connection()
    {
        var excluded = new CaptureSource("rcn", tenantId: "t", platform: "generic", media: "radio", streamUrl: "https://example.com/radio", isExcluded: true);
        var repository = new Repository(excluded);
        var monitor = new Monitor { IsOnline = false };
        var incidents = new Incidents();
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository, monitor);
        var service = new SourceAvailabilityReconciliationService(
            provider, new WorkingStream(), new NoPlugin(), new ServiceCollection().BuildServiceProvider(),
            new NoYouTube(), new NoAlert(), NullLogger<SourceAvailabilityReconciliationService>.Instance, incidents, monitor);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(incidents.Records);
        Assert.Empty(repository.Exclusions);
    }
}
