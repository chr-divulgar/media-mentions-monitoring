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

    // The archive account of each medium; a null one means that medium has no account.
    public sealed class Storages(IArchiveStorage? radio = null, IArchiveStorage? television = null, IArchiveStorage? press = null) : IArchiveStorageProvider
    {
        public static Storages All(IArchiveStorage storage) => new(storage, storage, storage);

        public IArchiveStorage? For(string medium) => medium switch
        {
            ArchiveMedia.Radio => radio,
            ArchiveMedia.Television => television,
            ArchiveMedia.Press => press,
            _ => null,
        };
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
    public void FindPendingFiles_returns_closed_tv_mp4_and_radio_opus_files_oldest_first()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var older = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 4), "rcn_tv", "rcn_tv_2026-10-04_17-00-00.mp4", Now.AddHours(-20));
            var newer = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 5), "rcn_tv", "rcn_tv_2026-10-05_10-00-00.mp4", Now.AddHours(-2));
            // Radio: the hourly opus goes up (re-encoded smaller, see Reencode); its transcription and resume file do not.
            var radioOpus = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_10-00-00.opus", Now.AddHours(-3));
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_10-00-00.json", Now.AddHours(-3));
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_09-00-00.opus.resume", Now.AddHours(-3));
            // A TV channel's audio and transcription are still not archived: only its video is.
            ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 5), "rcn_tv", "rcn_tv_2026-10-05_10-00-00.opus", Now.AddHours(-3));
            ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 5), "rcn_tv", "rcn_tv_2026-10-05_10-00-00.json", Now.AddHours(-3));

            var pending = ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20));

            Assert.Equal([older, radioOpus, newer], pending.Select(p => p.LocalPath));
            Assert.Equal("television/2026/10/04/rcn_tv/rcn_tv_2026-10-04_17-00-00.mp4", pending[0].RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindPendingFiles_sends_a_radio_opus_re_encoded_to_the_same_remote_path()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_10-00-00.opus", Now.AddHours(-3));
            ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 5), "rcn_tv", "rcn_tv_2026-10-05_10-00-04.mp4", Now.AddHours(-3));

            var pending = ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20));

            var radio = pending.Single(p => p.RelativePath.StartsWith("radio/", StringComparison.Ordinal));
            Assert.Equal("radio/2026/10/05/wradio/wradio_2026-10-05_10-00-00.opus", radio.RelativePath);
            // Same name in the archive as on disk: only the bytes are smaller.
            Assert.Equal(radio.RelativePath, radio.RemoteRelativePath);
            Assert.True(radio.Reencode);

            var video = pending.Single(p => p.RelativePath.StartsWith("television/", StringComparison.Ordinal));
            Assert.Equal(video.RelativePath, video.RemoteRelativePath);
            Assert.False(video.Reencode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindPendingFiles_waits_for_a_radio_hour_to_be_over_even_when_its_file_looks_idle()
    {
        // 14:55 Bogota. A station that dropped at 14:10 leaves its 14:00 file untouched for 45 minutes, but the hour is
        // still open (the session may come back and append to it); the 13:00 file is finished.
        var now = new DateTime(2026, 10, 5, 19, 55, 0, DateTimeKind.Utc);
        var root = ArchiveFixture.NewRoot();
        try
        {
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_14-00-00.opus", now.AddMinutes(-45));
            var closed = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 5), "wradio", "wradio_2026-10-05_13-00-00.opus", now.AddMinutes(-105));

            var pending = ArchiveUpload.FindPendingFiles(root, (_, _) => false, now, TimeSpan.FromMinutes(20));

            Assert.Equal([closed], pending.Select(p => p.LocalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("television/2026/10/05/rcn_tv/a.mp4", true)]
    [InlineData("radio/2026/10/05/wradio/wradio_2026-10-05_10-00-00.opus", true)]
    [InlineData("radio/2026/10/05/wradio/wradio_2026-10-05_10-00-00.json", false)]
    [InlineData("radio/2026/10/05/wradio/wradio_2026-10-05_09-00-00.opus.resume", false)]
    [InlineData("television/2026/10/05/rcn_tv/rcn_tv_2026-10-05_10-00-00.opus", false)]
    public void RequiresArchive_is_true_only_for_what_is_archived(string relativePath, bool expected) =>
        Assert.Equal(expected, ArchiveUpload.RequiresArchive(relativePath));

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

public sealed class ArchiveInProgressTests
{
    // 15:30 Bogota: the 15:00 recording is still open however old its mtime looks.
    private static readonly DateTime Now = new(2026, 10, 6, 20, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void FindPendingFiles_skips_the_hour_still_being_recorded_even_if_its_mtime_looks_idle()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var day = new DateOnly(2026, 10, 6);
            ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "rcn_tv_2026-10-06_15-00-04.mp4", Now.AddMinutes(-25));
            var closed = ArchiveFixture.MakeFile(root, "television", day, "rcn_tv", "rcn_tv_2026-10-06_14-00-07.mp4", Now.AddMinutes(-40));

            var pending = ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20));

            Assert.Equal([closed], pending.Select(p => p.LocalPath));
            Assert.Equal(2, ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now.AddMinutes(31), TimeSpan.FromMinutes(20)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class ArchiveSourceOffsetTests
{
    // 20:30 UTC. A Mexico City source (-6) is at 14:30 and still recording its 14:00 file; read as
    // Bogota time (15:30) that same file would look closed.
    private static readonly DateTime Now = new(2026, 10, 6, 20, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void FindPendingFiles_reads_the_file_name_in_the_sources_own_time_zone()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 6), "azteca_tv", "azteca_tv_2026-10-06_14-00-03.mp4", Now.AddMinutes(-25));
            var offsets = new Dictionary<string, TimeSpan> { ["azteca_tv"] = TimeSpan.FromHours(-6) };

            Assert.Empty(ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20), offsets));
            Assert.Single(ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now, TimeSpan.FromMinutes(20)));
            Assert.Single(ArchiveUpload.FindPendingFiles(root, (_, _) => false, Now.AddMinutes(31), TimeSpan.FromMinutes(20), offsets));
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

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ArchiveUploadWorker RadioWorker(
        string root, FakeStorage storage, ArchiveLedger ledger, Func<string, string, CancellationToken, Task> reencode) =>
        new(
            new OperationsWorkerOptions { AudioOutputRootPath = root, ArchiveMinIdleMinutes = 20, ArchiveMaxParallelUploads = 2 },
            ArchiveFixture.Storages.All(storage), ledger, new ArchiveFixture.Incidents(), NullLogger<ArchiveUploadWorker>.Instance,
            reencode: reencode);

    [Fact]
    public async Task RunOnceAsync_uploads_a_re_encoded_radio_hour_and_ledgers_the_local_opus()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 4), "wradio", "wradio_2026-10-04_10-00-00.opus", DateTime.UtcNow.AddHours(-30));
            var opusSize = new FileInfo(opus).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var storage = new FakeStorage();
            string? uploadedFrom = null;
            var worker = RadioWorker(root, storage, ledger, async (input, output, _) =>
            {
                Assert.Equal(opus, input);
                await File.WriteAllTextAsync(output, "opus-bytes");
                uploadedFrom = output;
            });

            await worker.RunOnceAsync(CancellationToken.None);

            Assert.Equal(["radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus"], storage.Uploaded);
            // The ledger keys the LOCAL file (path + size), which is what the purge asks about.
            Assert.True(ledger.Contains("radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus", opusSize));
            Assert.False(File.Exists(uploadedFrom!)); // the temporary re-encoded file is cleaned up
            Assert.True(File.Exists(opus));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnceAsync_sends_each_medium_to_its_own_account()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 4), "wradio", "wradio_2026-10-04_10-00-00.opus", DateTime.UtcNow.AddHours(-30));
            ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 4), "rcn_tv", "rcn_tv_2026-10-04_10-00-04.mp4", DateTime.UtcNow.AddHours(-30));
            var radio = new FakeStorage();
            var television = new FakeStorage();
            var worker = new ArchiveUploadWorker(
                new OperationsWorkerOptions { AudioOutputRootPath = root, ArchiveMinIdleMinutes = 20, ArchiveMaxParallelUploads = 2 },
                new ArchiveFixture.Storages(radio, television), new ArchiveLedger(Path.Combine(root, "ledger.jsonl")),
                new ArchiveFixture.Incidents(), NullLogger<ArchiveUploadWorker>.Instance,
                reencode: (_, output, _) => File.WriteAllTextAsync(output, "opus-bytes"));

            await worker.RunOnceAsync(CancellationToken.None);

            Assert.Equal(["radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus"], radio.Uploaded);
            Assert.Equal(["television/2026/10/04/rcn_tv/rcn_tv_2026-10-04_10-00-04.mp4"], television.Uploaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnceAsync_leaves_alone_a_medium_that_has_no_account_without_counting_it_as_a_failure()
    {
        // Nothing is uploaded, nothing is ledgered (so the purge keeps the file) and no failure streak is reported:
        // the purge's own backlog incident is what says a file is waiting for an account.
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 4), "wradio", "wradio_2026-10-04_10-00-00.opus", DateTime.UtcNow.AddHours(-30));
            var tv = ArchiveFixture.MakeFile(root, "television", new DateOnly(2026, 10, 4), "rcn_tv", "rcn_tv_2026-10-04_10-00-04.mp4", DateTime.UtcNow.AddHours(-30));
            var television = new FakeStorage();
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var incidents = new ArchiveFixture.Incidents();
            var worker = new ArchiveUploadWorker(
                new OperationsWorkerOptions { AudioOutputRootPath = root, ArchiveMinIdleMinutes = 20, ArchiveMaxParallelUploads = 2 },
                new ArchiveFixture.Storages(radio: null, television), ledger, incidents, NullLogger<ArchiveUploadWorker>.Instance,
                reencode: (_, output, _) => File.WriteAllTextAsync(output, "opus-bytes"));

            await worker.RunOnceAsync(CancellationToken.None);

            Assert.Single(television.Uploaded);
            Assert.False(ledger.Contains("radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus", new FileInfo(opus).Length));
            Assert.True(ledger.Contains("television/2026/10/04/rcn_tv/rcn_tv_2026-10-04_10-00-04.mp4", new FileInfo(tv).Length));
            Assert.DoesNotContain(incidents.Records, r => r.Kind == IncidentKinds.ArchiveFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnceAsync_does_not_ledger_a_radio_hour_whose_conversion_or_upload_failed()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = ArchiveFixture.MakeFile(root, "radio", new DateOnly(2026, 10, 4), "wradio", "wradio_2026-10-04_10-00-00.opus", DateTime.UtcNow.AddHours(-30));
            var size = new FileInfo(opus).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var storage = new FakeStorage();
            var broken = true;
            var worker = RadioWorker(root, storage, ledger, async (_, output, _) =>
            {
                if (broken) throw new InvalidOperationException("corrupt opus");
                await File.WriteAllTextAsync(output, "opus-bytes");
            });

            await worker.RunOnceAsync(CancellationToken.None); // conversion fails
            Assert.Empty(storage.Uploaded);
            Assert.False(ledger.Contains("radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus", size));

            broken = false;
            storage.Succeeds = false;
            await worker.RunOnceAsync(CancellationToken.None); // upload fails
            Assert.False(ledger.Contains("radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus", size));

            storage.Succeeds = true;
            await worker.RunOnceAsync(CancellationToken.None); // retried and archived
            Assert.True(ledger.Contains("radio/2026/10/04/wradio/wradio_2026-10-04_10-00-00.opus", size));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            var worker = new ArchiveUploadWorker(options, ArchiveFixture.Storages.All(storage), ledger, incidents, NullLogger<ArchiveUploadWorker>.Instance);

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
    public void Tick_deletes_files_that_are_not_archived_of_previous_days_without_needing_an_archive_copy()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var json = Audio(root, Yesterday, "a.json", TimeSpan.FromHours(5));
            var resume = Audio(root, Yesterday, "b.opus.resume", TimeSpan.FromHours(5));
            // A TV channel's audio is not archived either: it goes with its day.
            var tvOpus = ArchiveFixture.MakeFile(root, "television", Yesterday, "rcn_tv", "rcn_tv_2026-10-04_10-00-00.opus", ClockNow.UtcDateTime - TimeSpan.FromHours(5));

            Worker(root, new ArchiveLedger(Path.Combine(root, "ledger.jsonl")), new ArchiveFixture.Incidents()).Tick();

            Assert.False(File.Exists(json));
            Assert.False(File.Exists(resume));
            Assert.False(File.Exists(tvOpus));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tick_keeps_a_previous_day_radio_opus_until_the_archive_has_it_and_then_deletes_it()
    {
        var root = ArchiveFixture.NewRoot();
        try
        {
            var opus = Audio(root, Yesterday, "wradio_2026-10-04_10-00-00.opus", TimeSpan.FromHours(1));
            var size = new FileInfo(opus).Length;
            var ledger = new ArchiveLedger(Path.Combine(root, "ledger.jsonl"));
            var worker = Worker(root, ledger, new ArchiveFixture.Incidents());

            worker.Tick();
            Assert.True(File.Exists(opus)); // not archived yet

            ledger.Add(Relative(root, opus), size);
            worker.Tick();

            Assert.False(File.Exists(opus));
            Assert.False(ledger.Contains(Relative(root, opus), size));
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
            var stillWriting = Audio(root, Yesterday, "a.json", TimeSpan.FromMinutes(5));
            var finished = Audio(root, Yesterday, "b.json", TimeSpan.FromHours(2));

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
            Audio(root, Yesterday, "a.json", TimeSpan.FromHours(3));
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

public sealed class OpusArchiveReencodeTests
{
    private static string SampleOpus => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-radio.opus");

    [Fact]
    public void TranscodeFileToOpus_writes_a_small_mono_opus_as_long_as_the_whole_source()
    {
        var output = Path.Combine(Path.GetTempPath(), $"archive-test-{Guid.NewGuid():N}.opus");
        try
        {
            OpusSegmentTranscoder.TranscodeFileToOpus(SampleOpus, output, bitrateKbps: 24, sampleRateHz: 16000);

            var bytes = File.ReadAllBytes(output);
            Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(bytes, 0, 4)); // an Ogg container
            // 3 s at 24 kbps is ~9 KB; a header-only or silent-failure file would be far smaller.
            Assert.InRange(bytes.Length, 5_000, 14_000);

            var info = Mp4Info.Read(output);
            Assert.True(info.HasAudio);
            Assert.Equal("opus", info.AudioCodec);
            Assert.Equal(1, info.AudioChannels);

            // Read it back: mono 16-bit at 16 kHz is 32 000 bytes a second, after a 44-byte wav header.
            var wav = OpusSegmentTranscoder.Transcode([output], 0, 60, 16000, 64, "wav");
            Assert.InRange((wav.Length - 44) / 32000.0, 2.8, 3.3);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
