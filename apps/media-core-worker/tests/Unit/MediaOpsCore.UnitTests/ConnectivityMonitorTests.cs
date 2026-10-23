using System.Net;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ConnectivityMonitorTests
{
    private sealed class Incidents : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];

        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }

    private static ConnectivityMonitor Build(Func<bool> online, Incidents? incidents = null, bool enabled = true) =>
        new(enabled, TimeSpan.FromMinutes(1), _ => Task.FromResult(online()), incidents ?? new Incidents(), NullLogger<ConnectivityMonitor>.Instance);

    [Fact]
    public async Task A_probe_that_times_out_counts_as_offline_and_the_loop_waits_before_the_next_probe()
    {
        // HttpClient reports its timeout as a TaskCanceledException. Letting it out of the check made the loop log the error
        // and probe again at once, without waiting for the interval: a hot loop for as long as the network was bad.
        var probes = 0;
        var waits = new List<TimeSpan>();
        var monitor = new ConnectivityMonitor(
            true, TimeSpan.FromMinutes(1),
            _ =>
            {
                Interlocked.Increment(ref probes);
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            },
            new Incidents(), NullLogger<ConnectivityMonitor>.Instance,
            async (interval, cancellationToken) =>
            {
                lock (waits) waits.Add(interval);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            });

        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(1, probes);
        Assert.Equal([TimeSpan.FromMinutes(1)], waits);
        Assert.False(monitor.IsOnline);
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WaitUntilOnlineAsync_waits_while_offline_and_completes_when_the_connection_comes()
    {
        var online = false;
        var monitor = Build(() => online);

        await monitor.CheckOnceAsync(CancellationToken.None);
        var waiting = monitor.WaitUntilOnlineAsync();

        Assert.False(monitor.IsOnline);
        Assert.False(waiting.IsCompleted);

        online = true;
        await monitor.CheckOnceAsync(CancellationToken.None);

        Assert.True(monitor.IsOnline);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_loop_probes_again_every_interval_until_the_first_success()
    {
        var calls = 0;
        var intervals = new List<TimeSpan>();
        var monitor = new ConnectivityMonitor(
            true,
            TimeSpan.FromMinutes(1),
            _ => Task.FromResult(++calls >= 4),
            new Incidents(),
            NullLogger<ConnectivityMonitor>.Instance,
            async (interval, ct) =>
            {
                intervals.Add(interval);
                await Task.Delay(1, ct);
            });

        await monitor.StartAsync(CancellationToken.None);
        await monitor.WaitUntilOnlineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync(CancellationToken.None);

        Assert.True(calls >= 4);
        Assert.All(intervals, interval => Assert.Equal(TimeSpan.FromMinutes(1), interval));
    }

    [Fact]
    public async Task Being_offline_is_recorded_once_per_outage_and_so_is_the_return()
    {
        var online = false;
        var incidents = new Incidents();
        var monitor = Build(() => online, incidents);

        await monitor.CheckOnceAsync(CancellationToken.None);
        await monitor.CheckOnceAsync(CancellationToken.None);
        await monitor.CheckOnceAsync(CancellationToken.None);
        online = true;
        await monitor.CheckOnceAsync(CancellationToken.None);
        await monitor.CheckOnceAsync(CancellationToken.None);
        online = false;
        await monitor.CheckOnceAsync(CancellationToken.None);
        online = true;
        await monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(
            [IncidentKinds.ConnectionLost, IncidentKinds.ConnectionRestored, IncidentKinds.ConnectionLost, IncidentKinds.ConnectionRestored],
            incidents.Records.Select(record => record.Kind));
        Assert.All(incidents.Records, record => Assert.Equal(IncidentKinds.WorkerSourceId, record.SourceId));
    }

    [Fact]
    public async Task CameOnline_is_raised_when_the_connection_returns_but_not_at_the_first_connection()
    {
        var online = false;
        var monitor = Build(() => online);
        var raised = 0;
        monitor.CameOnline += () => raised++;

        await monitor.CheckOnceAsync(CancellationToken.None);
        online = true;
        await monitor.CheckOnceAsync(CancellationToken.None);
        Assert.Equal(0, raised);

        online = false;
        await monitor.CheckOnceAsync(CancellationToken.None);
        online = true;
        await monitor.CheckOnceAsync(CancellationToken.None);
        await monitor.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_offline()
    {
        var monitor = new ConnectivityMonitor(
            true, TimeSpan.FromMinutes(1), _ => throw new InvalidOperationException("boom"), new Incidents(), NullLogger<ConnectivityMonitor>.Instance);

        await monitor.CheckOnceAsync(CancellationToken.None);

        Assert.False(monitor.IsOnline);
    }

    [Fact]
    public async Task When_the_check_is_disabled_it_is_always_online_and_never_waits()
    {
        var monitor = Build(() => false, enabled: false);

        Assert.True(monitor.IsOnline);
        await monitor.WaitUntilOnlineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── the probe ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeHandler(Func<string, HttpStatusCode?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = answer(request.RequestUri!.ToString());
            return status is null
                ? throw new HttpRequestException("no route to host")
                : Task.FromResult(new HttpResponseMessage(status.Value));
        }
    }

    private static Task<bool> Probe(Func<string, HttpStatusCode?> answer, string? required = null) =>
        ConnectivityProbe.IsOnlineAsync(
            new HttpClient(new FakeHandler(answer)),
            ["https://a.test/", "https://b.test/"],
            required,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

    [Fact]
    public async Task The_probe_is_online_when_any_one_endpoint_answers_whatever_the_status()
    {
        Assert.True(await Probe(url => url.Contains("b.test") ? HttpStatusCode.InternalServerError : null));
        Assert.True(await Probe(_ => HttpStatusCode.NoContent));
    }

    [Fact]
    public async Task The_probe_is_offline_when_no_endpoint_answers()
    {
        Assert.False(await Probe(_ => null));
    }

    [Fact]
    public async Task The_probe_also_needs_the_required_endpoint_when_there_is_one()
    {
        const string required = "https://required.test/";
        Assert.False(await Probe(url => url.Contains("required") ? null : HttpStatusCode.NoContent, required));
        Assert.True(await Probe(_ => HttpStatusCode.NotFound, required));
    }

    [Fact]
    public void ParseUrls_keeps_valid_urls_and_falls_back_to_the_defaults()
    {
        Assert.Equal(["https://a.test/x", "http://b.test/"], ConnectivityProbe.ParseUrls(" https://a.test/x , nonsense, http://b.test/ "));
        Assert.Equal(ConnectivityProbe.DefaultUrls, ConnectivityProbe.ParseUrls(""));
        Assert.Equal(ConnectivityProbe.DefaultUrls, ConnectivityProbe.ParseUrls(null));
    }

    // ── what the source catalogue does while offline ─────────────────────────────────────────

    private sealed class StubMonitor(bool online) : IConnectivityMonitor
    {
        public bool IsOnline { get; set; } = online;

        public Task WaitUntilOnlineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event Action? CameOnline { add { } remove { } }
    }

    private sealed class Repository : ICaptureSourceRepository
    {
        public int Listings { get; private set; }
        public Dictionary<string, bool> Exclusions { get; } = [];

        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            Listings++;
            IReadOnlyList<CaptureSource> sources = [new CaptureSource("rcn", "rcn", "RCN", "radio", "https://x/live")];
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

    [Fact]
    public async Task The_catalogue_does_not_save_an_exclusion_while_offline_but_does_save_a_recovery()
    {
        var repository = new Repository();
        var monitor = new StubMonitor(online: false);
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository, monitor);

        Assert.False(await provider.PersistExclusionAsync("rcn", true));
        Assert.Empty(repository.Exclusions);

        // Clearing an exclusion is always safe to save.
        await provider.PersistExclusionAsync("rcn", false);
        Assert.False(repository.Exclusions["rcn"]);

        monitor.IsOnline = true;
        await provider.PersistExclusionAsync("rcn", true);
        Assert.True(repository.Exclusions["rcn"]);
    }

    [Fact]
    public void The_catalogue_is_not_read_or_remembered_while_offline()
    {
        var repository = new Repository();
        var monitor = new StubMonitor(online: false);
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository, monitor);

        Assert.Empty(provider.ListResolvedSources());
        Assert.Equal(0, repository.Listings);

        monitor.IsOnline = true;

        Assert.Single(provider.ListResolvedSources());
        Assert.Equal(1, repository.Listings);
    }
}
