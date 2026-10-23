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
    public void Tick_keeps_each_medium_for_its_own_days_and_only_today_of_television()
    {
        // Radio keeps 31 days, TV only the current one: the TV recordings of yesterday go, the radio of yesterday and
        // of a month ago stay.
        var root = Path.Combine(Path.GetTempPath(), $"retention-worker-{Guid.NewGuid():N}");
        try
        {
            var radioMonthAgo = MakeDayDir(root, "radio", new DateOnly(2026, 9, 10), "wradio");
            var radioExpired = MakeDayDir(root, "radio", new DateOnly(2026, 9, 2), "wradio");
            var radioYesterday = MakeDayDir(root, "radio", new DateOnly(2026, 10, 3), "wradio");
            var tvYesterday = MakeDayDir(root, "television", new DateOnly(2026, 10, 3), "rcn_tv");
            var tvToday = MakeDayDir(root, "television", new DateOnly(2026, 10, 4), "rcn_tv");

            var options = new OperationsWorkerOptions
            {
                AudioOutputRootPath = root, AudioRetentionDays = 31, AudioRetentionDaysTelevision = 0, MinFreeDiskGb = 0,
            };
            new RetentionPurgeWorker(
                options, new FakeIncidentRecorder(), NullLogger<RetentionPurgeWorker>.Instance, () => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-5)))
                .Tick();

            Assert.True(Directory.Exists(radioMonthAgo));
            Assert.False(Directory.Exists(radioExpired));
            Assert.True(Directory.Exists(radioYesterday));
            Assert.False(Directory.Exists(tvYesterday));
            Assert.True(Directory.Exists(tvToday));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_gives_television_the_general_retention_when_it_has_none_of_its_own()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-worker-{Guid.NewGuid():N}");
        try
        {
            var tvYesterday = MakeDayDir(root, "television", new DateOnly(2026, 10, 3), "rcn_tv");

            new RetentionPurgeWorker(
                new OperationsWorkerOptions { AudioOutputRootPath = root, AudioRetentionDays = 3, MinFreeDiskGb = 0 },
                new FakeIncidentRecorder(), NullLogger<RetentionPurgeWorker>.Instance, () => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-5))).Tick();

            Assert.True(Directory.Exists(tvYesterday));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_clears_the_stored_note_pdfs_with_the_bogota_day()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var days = new List<DateOnly>();
            var worker = new RetentionPurgeWorker(
                new OperationsWorkerOptions { AudioOutputRootPath = root, MinFreeDiskGb = 0 },
                new FakeIncidentRecorder(), NullLogger<RetentionPurgeWorker>.Instance,
                () => new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.FromHours(-5)),
                purgeNotePdfs: days.Add);

            worker.Tick();

            Assert.Equal([new DateOnly(2026, 10, 22)], days);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
