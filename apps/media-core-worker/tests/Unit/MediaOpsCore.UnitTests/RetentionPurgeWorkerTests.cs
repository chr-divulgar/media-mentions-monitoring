using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class RetentionPurgeWorkerTests
{
    private sealed class FakeIncidentRecorder : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];
        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }

    private static string MakeDayDir(string root, string media, DateOnly date, string sourceId)
    {
        var dir = Path.Combine(root, media, date.Year.ToString("D4"), date.Month.ToString("D2"), date.Day.ToString("D2"), sourceId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{sourceId}_{date:yyyy-MM-dd}_10-00-00.opus"), "fake");
        return dir;
    }

    [Fact]
    public void Tick_deletes_day_folders_older_than_the_configured_retention()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-worker-{Guid.NewGuid():N}");
        try
        {
            var oldSourceDir = MakeDayDir(root, "radio", new DateOnly(2026, 9, 1), "wradio");
            var recentSourceDir = MakeDayDir(root, "radio", new DateOnly(2026, 10, 3), "wradio");

            var options = new OperationsWorkerOptions { AudioOutputRootPath = root, AudioRetentionDays = 3, MinFreeDiskGb = 0 };
            var recorder = new FakeIncidentRecorder();
            var worker = new RetentionPurgeWorker(
                options, recorder, NullLogger<RetentionPurgeWorker>.Instance, () => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-5)));

            worker.Tick();

            Assert.False(Directory.Exists(oldSourceDir));
            Assert.True(Directory.Exists(recentSourceDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_records_a_disk_low_incident_once_and_a_recovered_incident_once_space_is_freed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var driveFreeGb = 5.0;
            var options = new OperationsWorkerOptions { AudioOutputRootPath = root, AudioRetentionDays = 30, MinFreeDiskGb = 10 };
            var recorder = new FakeIncidentRecorder();
            var worker = new RetentionPurgeWorker(
                options, recorder, NullLogger<RetentionPurgeWorker>.Instance, () => DateTimeOffset.UtcNow, () => driveFreeGb);

            worker.Tick();
            worker.Tick(); // still low — must not log a second disk-low incident

            Assert.Single(recorder.Records, r => r.Kind == IncidentKinds.DiskLow);

            driveFreeGb = 50.0;
            worker.Tick();

            Assert.Single(recorder.Records, r => r.Kind == IncidentKinds.DiskRecovered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
