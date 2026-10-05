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
    private static readonly DateTime Now = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FindPendingFiles_returns_closed_capture_files_oldest_first_with_slash_relative_paths()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var day = new DateOnly(2026, 10, 4);
            var newer = ArchiveFixture.MakeFile(root, "radio", day, "wradio", "wradio_2026-10-04_18-00-00.opus", Now.AddHours(-1));
            var older = ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "rcn_tv_2026-10-04_17-00-00.mp4", Now.AddHours(-2));

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
    public void FindPendingFiles_skips_files_still_being_written_resume_files_and_already_archived_ones()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var day = new DateOnly(2026, 10, 4);
            ArchiveFixture.MakeFile(root, "radio", day, "wradio", "active.opus", Now.AddMinutes(-2));
            ArchiveFixture.MakeFile(root, "radio", day, "wradio", "old.opus.resume", Now.AddHours(-3));
            ArchiveFixture.MakeFile(root, "radio", day, "wradio", "done.opus", Now.AddHours(-3));
            var keep = ArchiveFixture.MakeFile(root, "radio", day, "wradio", "todo.json", Now.AddHours(-3));

            var pending = ArchiveUpload.FindPendingFiles(
                root, (relative, _) => relative.EndsWith("done.opus", StringComparison.Ordinal), Now, TimeSpan.FromMinutes(20));

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
    }

    [Fact]
    public async Task RunOnceAsync_records_only_verified_uploads_and_reports_a_failure_streak_once()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var file = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 4), "wradio", "a.opus", DateTime.UtcNow.AddHours(-2));
            var size = new FileInfo(file).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var storage = new FakeStorage { Succeeds = false };
            var incidents = new ArchiveFixture.Incidents();
            var options = new OperationsWorkerOptions { AudioOutputRootPath = root, ArchiveMinIdleMinutes = 20, ArchiveMaxParallelUploads = 2 };
            var worker = new ArchiveUploadWorker(options, storage, ledger, incidents, NullLogger<ArchiveUploadWorker>.Instance);

            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None);

            Assert.False(ledger.Contains("radio/2026/10/04/wradio/a.opus", size));
            Assert.Single(incidents.Records, r => r.Kind == IncidentKinds.ArchiveFailed);

            storage.Succeeds = true;
            await worker.RunOnceAsync(CancellationToken.None);
            Assert.True(ledger.Contains("radio/2026/10/04/wradio/a.opus", size));

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
    private static RetentionPurgeWorker Worker(string root, ArchiveLedger ledger, ArchiveFixture.Incidents incidents) =>
        new(
            new OperationsWorkerOptions { AudioOutputRootPath = root, AudioRetentionDays = 2, MinFreeDiskGb = 0 },
            incidents, NullLogger<RetentionPurgeWorker>.Instance,
            () => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-5)), () => 100, ledger);

    [Fact]
    public void Tick_deletes_only_archived_old_files_and_flags_the_unarchived_backlog()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var oldDay = new DateOnly(2026, 9, 25);
            var archived = ArchiveFixture.MakeFile(root, "radio", oldDay, "wradio", "a.opus", DateTime.UtcNow.AddDays(-9));
            var notArchived = ArchiveFixture.MakeFile(root, "radio", oldDay, "wradio", "b.opus", DateTime.UtcNow.AddDays(-9));
            var archivedSize = new FileInfo(archived).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            ledger.Add("radio/2026/09/25/wradio/a.opus", archivedSize);
            var incidents = new ArchiveFixture.Incidents();

            Worker(root, ledger, incidents).Tick();

            Assert.False(File.Exists(archived));
            Assert.True(File.Exists(notArchived));
            Assert.False(ledger.Contains("radio/2026/09/25/wradio/a.opus", archivedSize));
            Assert.Single(incidents.Records, r => r.Kind == IncidentKinds.ArchiveBacklog);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_removes_a_day_folder_once_everything_in_it_was_archived_and_purged()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var archived = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 9, 25), "wradio", "a.opus", DateTime.UtcNow.AddDays(-9));
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            ledger.Add("radio/2026/09/25/wradio/a.opus", new FileInfo(archived).Length);

            Worker(root, ledger, new ArchiveFixture.Incidents()).Tick();

            Assert.False(Directory.Exists(Path.Combine(root, "radio", "2026", "09", "25")));
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
