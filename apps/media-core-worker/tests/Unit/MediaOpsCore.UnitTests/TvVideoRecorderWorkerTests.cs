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

    // Simulates the recording taking real time: the shared clock jumps by however long ffmpeg ran.
    private sealed class FakeRunner(ProcessExecutionResult result, Action<TimeSpan> advanceClock, bool runsFullDuration) : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];
        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            advanceClock(runsFullDuration ? command.Timeout!.Value - TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(3));
            return Task.FromResult(result);
        }
    }

    private static CaptureSource TvSource() =>
        new("noticias-caracol-live", "youtube", "Noticias Caracol", "television", "https://stale.example/live.m3u8");

    private static (TvVideoRecorderWorker Worker, FakeRunner Runner, FakeIncidents Incidents, string Root) Build(
        IReadOnlyList<string> resolvedUrls, ProcessExecutionResult result, bool runsFullDuration = true)
    {
        var now = new DateTimeOffset(2026, 10, 4, 14, 20, 0, TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), $"tv-video-{Guid.NewGuid():N}");
        var options = new OperationsWorkerOptions { AudioOutputRootPath = root, FfmpegExecutablePath = "ffmpeg", TvVideoMaxHeight = 480 };
        var runner = new FakeRunner(result, elapsed => now += elapsed, runsFullDuration);
        var incidents = new FakeIncidents();
        var worker = new TvVideoRecorderWorker(
            options, runner, incidents, NullLogger<TvVideoRecorderWorker>.Instance,
            (_, _) => Task.FromResult(resolvedUrls),
            () => now);
        return (worker, runner, incidents, root);
    }

    [Fact]
    public async Task RecordOnceAsync_runs_ffmpeg_on_the_resolved_url_into_the_source_folder()
    {
        var (worker, runner, incidents, root) = Build(["https://fresh.example/video.m3u8", "https://fresh.example/audio.m3u8"], new ProcessExecutionResult(0, "", "", TimedOut: false));
        try
        {
            var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

            Assert.True(ok);
            var command = Assert.Single(runner.Commands);
            Assert.Equal("ffmpeg", command.FileName);
            Assert.Contains("https://fresh.example/video.m3u8", command.Arguments);
            Assert.Contains("https://fresh.example/audio.m3u8", command.Arguments);
            Assert.Contains("1:a:0", command.Arguments);
            Assert.StartsWith(Path.Combine(root, "television"), command.Arguments[^1]);
            Assert.EndsWith(".mp4", command.Arguments[^1]);
            Assert.True(Directory.Exists(Path.GetDirectoryName(command.Arguments[^1])));
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
        var (worker, runner, incidents, root) = Build([], new ProcessExecutionResult(0, "", "", TimedOut: false));

        var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(runner.Commands);
        var incident = Assert.Single(incidents.Records);
        Assert.Equal(IncidentKinds.Failed, incident.Kind);
        Assert.Contains("video", incident.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordOnceAsync_records_a_failure_incident_when_ffmpeg_fails()
    {
        var (worker, _, incidents, root) = Build(
            ["https://fresh.example/live.m3u8"], new ProcessExecutionResult(1, "", "Server returned 403 Forbidden", TimedOut: false), runsFullDuration: false);
        try
        {
            var ok = await worker.RecordOnceAsync(TvSource(), CancellationToken.None);

            Assert.False(ok);
            var incident = Assert.Single(incidents.Records);
            Assert.Contains("403", incident.Reason);
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
