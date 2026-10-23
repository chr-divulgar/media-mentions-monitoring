using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class TvVideoRecorderWorkerTests
{
    private sealed class FakeIncidents : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];
        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }

    private sealed class FakeRecorder(string? stopReason)
    {
        public List<(IReadOnlyList<string> Urls, TimeSpan Duration, string Path)> Calls { get; } = [];

        public Task<string?> RecordAsync(IReadOnlyList<string> urls, TimeSpan duration, string path, CancellationToken ct)
        {
            Calls.Add((urls, duration, path));
            return Task.FromResult(stopReason);
        }
    }

    private sealed class FakeProvider(params CaptureSource[] sources) : ICaptureSourceProvider
    {
        public List<CaptureSource> Active { get; } = [.. sources];

        public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureSource>>(Active.ToArray());
    }

    private sealed class TimingOutProvider : ICaptureSourceProvider
    {
        public int Calls;

        public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
        }
    }

    [Fact]
    public async Task The_worker_keeps_running_when_refreshing_the_sources_times_out()
    {
        var provider = new TimingOutProvider();
        var (worker, _, _, root) = Build(["https://fresh.example/video.m3u8"], stopReason: null, provider);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(300);

            Assert.True(provider.Calls >= 1);
            Assert.False(worker.ExecuteTask!.IsCompleted); // a timeout is a failed refresh, not the end of the worker
            await worker.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CaptureSource TvSource() =>
        new("noticias-caracol-live", "youtube", "Noticias Caracol", "television", "https://stale.example/live.m3u8");

    private static (TvVideoRecorderWorker Worker, FakeRecorder Recorder, FakeIncidents Incidents, string Root) Build(
        IReadOnlyList<string> resolvedUrls, string? stopReason, ICaptureSourceProvider? provider = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tv-video-{Guid.NewGuid():N}");
        var options = new OperationsWorkerOptions { AudioOutputRootPath = root, TvVideoMaxHeight = 480 };
        var recorder = new FakeRecorder(stopReason);
        var incidents = new FakeIncidents();
        var worker = new TvVideoRecorderWorker(
            options, provider ?? new FakeProvider(TvSource()), incidents, NullLogger<TvVideoRecorderWorker>.Instance,
            (_, _) => Task.FromResult(resolvedUrls),
            recorder.RecordAsync);
        return (worker, recorder, incidents, root);
    }

    [Fact]
    public async Task RecordOnceAsync_records_the_resolved_streams_into_the_source_folder_until_the_hour_turns()
    {
        var (worker, recorder, incidents, root) = Build(["https://fresh.example/video.m3u8", "https://fresh.example/audio.m3u8"], stopReason: null);
        try
        {
            var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

            Assert.True(ok);
            var call = Assert.Single(recorder.Calls);
            Assert.Equal(["https://fresh.example/video.m3u8", "https://fresh.example/audio.m3u8"], call.Urls);
            Assert.StartsWith(Path.Combine(root, "television"), call.Path);
            Assert.EndsWith(".mp4", call.Path);
            Assert.True(call.Duration > TimeSpan.Zero && call.Duration <= TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
            Assert.True(Directory.Exists(Path.GetDirectoryName(call.Path)));
            Assert.Empty(incidents.Records);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordOnceAsync_records_a_failure_incident_when_no_video_url_can_be_resolved()
    {
        var (worker, recorder, incidents, _) = Build([], stopReason: null);

        var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(recorder.Calls);
        var incident = Assert.Single(incidents.Records);
        Assert.Equal(IncidentKinds.Failed, incident.Kind);
        Assert.Contains("video", incident.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordOnceAsync_records_a_failure_incident_when_the_recording_stops_early()
    {
        var (worker, _, incidents, root) = Build(["https://fresh.example/live.m3u8"], stopReason: "el stream terminó antes de completar la hora");
        try
        {
            var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("terminó", Assert.Single(incidents.Records).Reason);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class TvVideoRecorderReconcileTests
{
    private sealed class FakeProvider : ICaptureSourceProvider
    {
        public List<CaptureSource> Active { get; } = [];

        public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureSource>>(Active.ToArray());
    }

    private sealed class NullIncidents : ISourceIncidentRecorder
    {
        public void Record(string sourceId, string kind, string? reason = null)
        {
        }
    }

    [Fact]
    public async Task ReconcileAsync_starts_a_channel_that_was_not_active_at_startup_once_it_recovers()
    {
        // Regression: the recorder used to read the TV list once at startup and drop excluded
        // channels; startup validation excludes channels that are briefly down, and the
        // reconciliation recovers them minutes later — they then never got MP4 at all.
        var root = Path.Combine(Path.GetTempPath(), $"tv-video-{Guid.NewGuid():N}");
        var provider = new FakeProvider();
        var recorded = new List<string>();
        using var cts = new CancellationTokenSource();
        var worker = new TvVideoRecorderWorker(
            new OperationsWorkerOptions { AudioOutputRootPath = root },
            provider, new NullIncidents(), NullLogger<TvVideoRecorderWorker>.Instance,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["https://live.example/v.m3u8"]),
            (_, _, path, _) =>
            {
                recorded.Add(path);
                cts.Cancel(); // one hour is enough for the test
                return Task.FromResult<string?>(null);
            });
        try
        {
            Assert.Empty(await worker.ReconcileAsync(cts.Token)); // startup: channel excluded, nothing to record

            provider.Active.Add(new CaptureSource("rcn_tv", "youtube", "RCN", "television", "https://www.youtube.com/@NoticiasRCN/live"));
            provider.Active.Add(new CaptureSource("wradio", "web", "W Radio", "radio", "https://radio.example/stream"));
            var started = await worker.ReconcileAsync(cts.Token);
            await Task.WhenAll(started);

            await Assert.Single(started); // only the TV source, never the radio one
            Assert.Contains("rcn_tv", Assert.Single(recorded));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileAsync_waits_for_the_channels_audio_to_be_capturing_before_recording_its_video()
    {
        // The audio capture is the signal that the channel is live: the video starts with it, instead of
        // trailing it by however long the recorder's own polling takes.
        var root = Path.Combine(Path.GetTempPath(), $"tv-video-{Guid.NewGuid():N}");
        var capturing = new HashSet<string>();
        var provider = new FakeProvider();
        var recorded = new List<string>();
        using var cts = new CancellationTokenSource();
        var worker = new TvVideoRecorderWorker(
            new OperationsWorkerOptions { AudioOutputRootPath = root },
            provider, new NullIncidents(), NullLogger<TvVideoRecorderWorker>.Instance,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["https://live.example/v.m3u8"]),
            (_, _, path, _) =>
            {
                recorded.Add(path);
                cts.Cancel();
                return Task.FromResult<string?>(null);
            },
            isAudioCapturing: capturing.Contains);
        try
        {
            provider.Active.Add(new CaptureSource("noticias-caracol-live", "youtube", "Noticias Caracol", "television", "https://www.youtube.com/@NoticiasCaracol/live"));
            Assert.Empty(await worker.ReconcileAsync(cts.Token)); // channel off air: no audio, no video

            capturing.Add("noticias-caracol-live");
            await Task.WhenAll(await worker.ReconcileAsync(cts.Token));

            Assert.Contains("noticias-caracol-live", Assert.Single(recorded));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class LocalSystemProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_kills_the_child_and_throws_when_cancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalSystemProcessRunner().RunAsync(
                new ProcessCommand("cmd", ["/c", "ping -n 30 127.0.0.1 >nul"], timeout: TimeSpan.FromMinutes(1)), cts.Token));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
