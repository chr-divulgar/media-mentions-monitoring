using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

internal static class ArchiveFixture
{
    public static string NewRoot() => Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");

    public static string MakeFile(string root, string media, DateOnly date, string sourceId, string name, DateTime lastWriteUtc)
    {
        var dir = Path.Combine(root, media, date.Year.ToString("D4"), date.Month.ToString("D2"), date.Day.ToString("D2"), sourceId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "fake-" + name);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    public sealed class Incidents : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];
        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }
}

public sealed class ArchiveUploadTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FindPendingFiles_returns_only_closed_mp4_files_from_today_and_earlier_days_oldest_first()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var older = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 4), "rcn_tv", "rcn_tv_2026-10-04_17-00-00.mp4", Now.AddHours(-20));
            var newer = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 5), "rcn_tv", "rcn_tv_2026-10-05_17-00-00.mp4", Now.AddHours(-2));
            // Audio is not archived at all: not the opus, not its transcription, not a resume file.
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_17-00-00.opus", Now.AddHours(-2));
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_17-00-00.json", Now.AddHours(-2));
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_16-00-00.opus.resume", Now.AddHours(-2));

            var pending = ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20));

            Assert.Equal([older, newer], pending.Select(p => p.LocalPath));
            Assert.Equal("television/2026/10/04/rcn_tv/rcn_tv_2026-10-04_17-00-00.mp4", pending[0].RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindPendingFiles_skips_mp4_files_still_being_written_and_already_archived_ones()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var day = new DateOnly(2026, 10, 5);
            ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "active.mp4", Now.AddMinutes(-2));
            ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "done.mp4", Now.AddHours(-3));
            var keep = ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "todo.mp4", Now.AddHours(-3));

            var pending = ArchiveUpload.FindPendingFiles(
                root, (relative, _) => relative.EndsWith("done.mp4", StringComparison.Ordinal), Now, TimeSpan.FromMinutes(20));

            Assert.Equal([keep], pending.Select(p => p.LocalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class ArchiveLedgerTests
{
    [Fact]
    public void Ledger_persists_entries_and_reloads_them_after_a_restart()
    {
        var dir = ArchiveFixture.NewRoot();
        try
        {
            var path = Path.Combine(dir, "archive-ledger.jsonl");
            var ledger = new ArchiveLedger(path);
            ledger.Add("radio/2026/10/04/a/a.opus", 120);
            ledger.Add("radio/2026/10/04/a/b.opus", 50);
            ledger.Remove("radio/2026/10/04/a/b.opus");

            var reloaded = new ArchiveLedger(path);

            Assert.True(reloaded.Contains("radio/2026/10/04/a/a.opus", 120));
            // Same path, different size: the local file is not the copy that was archived.
            Assert.False(reloaded.Contains("radio/2026/10/04/a/a.opus", 999));
            Assert.False(reloaded.Contains("radio/2026/10/04/a/b.opus", 50));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class ArchiveUploadWorkerTests
{
    private sealed class FakeStorage : IArchiveStorage
    {
        public bool Succeeds { get; set; } = true;
        public List<string> Uploaded { get; } = [];

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default)
        {
            Uploaded.Add(relativePath);
            return Task.FromResult(Succeeds);
        }

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task RunOnceAsync_records_only_verified_uploads_and_reports_a_failure_streak_once()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var file = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 4), "rcn_tv", "a.mp4", DateTime.UtcNow.AddHours(-2));
            var size = new FileInfo(file).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var storage = new FakeStorage { Succeeds = false };
            var incidents = new ArchiveFixture.Incidents();
            var options = new OperationsWorkerOptions { AudioOutputRootPath = root, ArchiveMinIdleMinutes = 20, ArchiveMaxParallelUploads = 2 };
            var worker = new ArchiveUploadWorker(options, storage, ledger, incidents, NullLogger<ArchiveUploadWorker>.Instance);

            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None);

            Assert.False(ledger.Contains("television/2026/10/04/rcn_tv/a.mp4", size));
            Assert.Single(incidents.Records, r => r.Kind == IncidentKinds.ArchiveFailed);

            storage.Succeeds = true;
            await worker.RunOnceAsync(CancellationToken.None);
            Assert.True(ledger.Contains("television/2026/10/04/rcn_tv/a.mp4", size));

            await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(3, storage.Uploaded.Count); // once archived, never uploaded again
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class RetentionPurgeWithArchiveTests
{
    // 12:00 in Bogota on the 5th: "today" is 2026-10-05, "yesterday" 2026-10-04.
    private static readonly DateTimeOffset ClockNow = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly DateOnly Yesterday = new(2026, 10, 4);

    private static RetentionPurgeWorker Worker(string root, ArchiveLedger ledger, ArchiveFixture.Incidents incidents) =>
        new(
            // Retention 0: keep only today. ArchiveMinIdleMinutes (20) also shields files still being written.
            new OperationsWorkerOptions { AudioOutputRootPath = root, AudioRetentionDays = 0, MinFreeDiskGb = 0 },
            incidents, NullLogger<RetentionPurgeWorker>.Instance, () => ClockNow, () => 100, ledger);

    private static string Mp4(string root, DateOnly day, string name, TimeSpan age) =>
        ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", name, ClockNow.UtcDateTime - age);

    private static string Audio(string root, DateOnly day, string name, TimeSpan age) =>
        ArchiveFixture.MakeFile(root, "radio", day, "wradio", name, ClockNow.UtcDateTime - age);

    private static string Relative(string root, string file) => ArchiveUpload.ToRelativePath(root, file);

    [Fact]
    public void Tick_deletes_non_mp4_files_of_previous_days_without_needing_an_archive_copy()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = Audio(root, Yesterday, "a.opus", TimeSpan.FromHours(5));
            var json = Audio(root, Yesterday, "a.json", TimeSpan.FromHours(5));
            var resume = Audio(root, Yesterday, "b.opus.resume", TimeSpan.FromHours(5));

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), new ArchiveFixture.Incidents()).Tick();

            Assert.False(File.Exists(opus));
            Assert.False(File.Exists(json));
            Assert.False(File.Exists(resume));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_keeps_a_previous_day_mp4_that_is_not_in_the_archive()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var mp4 = Mp4(root, Yesterday, "a.mp4", TimeSpan.FromHours(1));

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), new ArchiveFixture.Incidents()).Tick();

            Assert.True(File.Exists(mp4));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_deletes_an_archived_previous_day_mp4_and_forgets_it_in_the_ledger()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var mp4 = Mp4(root, Yesterday, "a.mp4", TimeSpan.FromHours(1));
            var size = new FileInfo(mp4).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            ledger.Add(Relative(root, mp4), size);

            Worker(root, ledger, new ArchiveFixture.Incidents()).Tick();

            Assert.False(File.Exists(mp4));
            Assert.False(ledger.Contains(Relative(root, mp4), size));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_never_deletes_anything_from_today_not_even_an_mp4_that_is_already_archived()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = Audio(root, Today, "a.opus", TimeSpan.FromHours(3));
            var json = Audio(root, Today, "a.json", TimeSpan.FromHours(3));
            var mp4 = Mp4(root, Today, "a.mp4", TimeSpan.FromHours(3));
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            ledger.Add(Relative(root, mp4), new FileInfo(mp4).Length);

            Worker(root, ledger, new ArchiveFixture.Incidents()).Tick();

            Assert.True(File.Exists(opus));
            Assert.True(File.Exists(json));
            Assert.True(File.Exists(mp4));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_leaves_a_previous_day_file_that_was_written_to_recently()
    {
        // Just after midnight the last hour of the day can still be flushing: yesterday's folder does
        // not mean yesterday's file is finished.
        var root = ArchiveFixture.NewRoot();
        try
        {
            var stillWriting = Audio(root, Yesterday, "a.opus", TimeSpan.FromMinutes(5));
            var finished = Audio(root, Yesterday, "b.opus", TimeSpan.FromHours(2));

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), new ArchiveFixture.Incidents()).Tick();

            Assert.True(File.Exists(stillWriting));
            Assert.False(File.Exists(finished));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_does_not_flag_a_backlog_for_an_mp4_that_only_just_closed()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            Mp4(root, Yesterday, "a.mp4", TimeSpan.FromMinutes(40)); // closed, waiting its turn to upload
            var incidents = new ArchiveFixture.Incidents();

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), incidents).Tick();

            Assert.DoesNotContain(incidents.Records, r => r.Kind == IncidentKinds.ArchiveBacklog);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_flags_a_backlog_for_an_mp4_that_has_stayed_unarchived_for_over_two_hours()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var mp4 = Mp4(root, Yesterday, "a.mp4", TimeSpan.FromHours(3));
            var incidents = new ArchiveFixture.Incidents();

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), incidents).Tick();

            Assert.True(File.Exists(mp4));
            Assert.Single(incidents.Records, r => r.Kind == IncidentKinds.ArchiveBacklog);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_removes_a_previous_day_folder_once_nothing_is_left_in_it()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var mp4 = Mp4(root, Yesterday, "a.mp4", TimeSpan.FromHours(3));
            Audio(root, Yesterday, "a.opus", TimeSpan.FromHours(3));
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            ledger.Add(Relative(root, mp4), new FileInfo(mp4).Length);

            Worker(root, ledger, new ArchiveFixture.Incidents()).Tick();

            Assert.False(Directory.Exists(Path.Combine(root, "radio", "2026", "10", "04")));
            Assert.False(Directory.Exists(Path.Combine(root, "television", "2026", "10", "04")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class FtpArchiveCredentialsTests
{
    [Theory]
    [InlineData("ftp://archivo.example.com/monitoreo/", "archivo.example.com", 21, "/monitoreo", false)]
    [InlineData("ftp://archivo.example.com:2121/a/b", "archivo.example.com", 2121, "/a/b", false)]
    [InlineData("ftps://archivo.example.com/x", "archivo.example.com", 990, "/x", true)]
    [InlineData("ftp://archivo.example.com", "archivo.example.com", 21, "", false)]
    public void Parse_reads_host_port_base_path_and_implicit_tls(string url, string host, int port, string basePath, bool isImplicit)
    {
        var parsed = new FtpArchiveCredentials(url, "u", "p").Parse();

        Assert.Equal((host, port, basePath, isImplicit), parsed);
    }
}
