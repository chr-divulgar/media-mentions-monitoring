using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The one-time conversion of the radio hours already on disk to 32 kbps, run from the service on demand. The encoder
// and the Ogg reader are injected, so what is decided here is what happens to the file, the two ledgers and the status.
public sealed class RadioOpusConversionJobTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 23, 18, 30, 0, DateTimeKind.Utc);
    private const long Big = 28_000_000;

    private readonly string root = Path.Combine(Path.GetTempPath(), "radio-job-" + Guid.NewGuid().ToString("N"));
    private readonly string stage;
    private readonly ArchiveLedger archiveLedger;
    private readonly ArchiveLedger convertedLedger;

    public RadioOpusConversionJobTests()
    {
        stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        archiveLedger = new ArchiveLedger(Path.Combine(stage, "archive-ledger.jsonl"));
        convertedLedger = new ArchiveLedger(Path.Combine(stage, "converted-ledger.jsonl"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Write(string day, string source, string hour, long size = Big, DateTime? lastWriteUtc = null)
    {
        var date = DateTime.Parse(day);
        var directory = Path.Combine(root, "radio", $"{date:yyyy}", $"{date:MM}", $"{date:dd}", source);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{source}_{day}_{hour}.opus");
        using (var stream = File.Create(path))
        {
            stream.SetLength(size);
        }

        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? Now.AddDays(-5));
        return path;
    }

    private string Relative(string path) => ArchiveUpload.ToRelativePath(root, path);

    private static void HalveTheSize(string input, string output) => File.WriteAllBytes(output, new byte[new FileInfo(input).Length / 2]);

    // Every file lasts an hour, so its bitrate follows its size: 28 MB is 62 kbps, 14 MB is 31 kbps.
    private static OggOpusInfo? AnHour(string path) => new(1, 16000, TimeSpan.FromSeconds(3600));

    private RadioOpusConversionJob Build(
        Action<string, string>? reencode = null,
        Func<string, OggOpusInfo?>? readInfo = null,
        int parallel = 1,
        long minBytes = 21_000_000)
    {
        var options = new OperationsWorkerOptions
        {
            AudioOutputRootPath = root,
            StageFilesystemRootPath = stage,
            RadioOpusConversionMaxParallel = parallel,
            ArchiveMinIdleMinutes = 20,
        };
        return new RadioOpusConversionJob(
            options, archiveLedger, convertedLedger, NullLogger<RadioOpusConversionJob>.Instance,
            offsets: _ => Task.FromResult<IReadOnlyDictionary<string, TimeSpan>?>(null),
            reencode: reencode ?? HalveTheSize,
            readInfo: readInfo ?? AnHour,
            minBytes: minBytes,
            clock: () => Now);
    }

    [Fact]
    public async Task Rewrites_each_hour_smaller_in_the_same_place_and_remembers_it()
    {
        var a = Write("2026-11-20", "A", "09-00-00");
        var b = Write("2026-11-20", "B", "09-00-00");
        var job = Build();

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(Big / 2, new FileInfo(a).Length);
        Assert.Equal(Big / 2, new FileInfo(b).Length);
        Assert.True(convertedLedger.Contains(Relative(a), Big / 2));
        var status = job.Status;
        Assert.Equal(2, status.Converted);
        Assert.Equal(2 * Big, status.BytesBefore);
        Assert.Equal(Big, status.BytesAfter);
        Assert.Equal("finished", status.State);
    }

    [Fact]
    public async Task Tells_the_archive_ledger_the_new_size_of_what_was_archived_and_adds_nothing_for_the_rest()
    {
        var archived = Write("2026-11-20", "A", "09-00-00");
        var notYet = Write("2026-11-20", "B", "09-00-00");
        archiveLedger.Add(Relative(archived), Big);

        await Build().RunAsync(dryRun: false, limit: null, CancellationToken.None);

        // Without this the archive would see a file it does not have at that size and upload it again.
        Assert.True(archiveLedger.Contains(Relative(archived), Big / 2));
        Assert.False(archiveLedger.Contains(Relative(archived), Big));
        Assert.False(archiveLedger.Contains(Relative(notYet), Big / 2));
        Assert.False(archiveLedger.Contains(Relative(notYet), Big));
    }

    [Fact]
    public async Task Leaves_alone_a_file_whose_real_bitrate_is_already_low()
    {
        var path = Write("2026-11-20", "A", "09-00-00");
        var job = Build(readInfo: _ => new OggOpusInfo(1, 16000, TimeSpan.FromSeconds(7200))); // 28 MB in 2 h: 31 kbps

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(Big, new FileInfo(path).Length);
        Assert.Equal(1, job.Status.Skipped);
        Assert.Equal(0, job.Status.Converted);
    }

    [Fact]
    public async Task A_trial_run_leaves_the_originals_and_the_ledgers_untouched_and_keeps_the_result_to_listen_to()
    {
        var path = Write("2026-11-20", "A", "09-00-00");
        archiveLedger.Add(Relative(path), Big);
        var job = Build();

        await job.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal(Big, new FileInfo(path).Length);
        Assert.True(archiveLedger.Contains(Relative(path), Big));
        Assert.False(convertedLedger.Contains(Relative(path), Big / 2));
        var preview = Assert.Single(Directory.GetFiles(Path.Combine(stage, "radio-opus-preview")));
        Assert.Equal(Big / 2, new FileInfo(preview).Length);
        Assert.True(job.Status.DryRun);
        Assert.Equal(1, job.Status.Converted);
    }

    [Fact]
    public async Task Keeps_the_original_when_the_new_file_does_not_last_as_long()
    {
        var path = Write("2026-11-20", "A", "09-00-00");
        var job = Build(readInfo: file => new OggOpusInfo(1, 16000, TimeSpan.FromSeconds(file.EndsWith(".convert.tmp") ? 3000 : 3600)));

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(Big, new FileInfo(path).Length);
        Assert.Equal(1, job.Status.Failed);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.convert.tmp"));
    }

    [Fact]
    public async Task Keeps_the_original_when_the_new_file_is_not_smaller()
    {
        var path = Write("2026-11-20", "A", "09-00-00");
        var job = Build(reencode: (input, output) => File.Copy(input, output));

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(Big, new FileInfo(path).Length);
        Assert.Equal(1, job.Status.Failed);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.convert.tmp"));
    }

    [Fact]
    public async Task One_file_that_cannot_be_encoded_does_not_stop_the_others()
    {
        var bad = Write("2026-11-21", "A", "09-00-00");
        var good = Write("2026-11-20", "A", "09-00-00");
        var job = Build(reencode: (input, output) =>
        {
            if (input == bad) throw new InvalidOperationException("corrupt");
            HalveTheSize(input, output);
        });

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(Big, new FileInfo(bad).Length);
        Assert.Equal(Big / 2, new FileInfo(good).Length);
        Assert.Equal(1, job.Status.Failed);
        Assert.Equal(1, job.Status.Converted);
    }

    [Fact]
    public async Task A_file_someone_is_reading_is_left_for_later_and_the_archive_ledger_goes_back()
    {
        var busy = Write("2026-11-21", "A", "09-00-00");
        var free = Write("2026-11-20", "A", "09-00-00");
        archiveLedger.Add(Relative(busy), Big);
        var job = Build();

        long sizeAfter;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read)) // an open reader blocks the replacement
        {
            await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);
            sizeAfter = new FileInfo(busy).Length;
        }

        Assert.Equal(Big, sizeAfter);
        Assert.True(archiveLedger.Contains(Relative(busy), Big));
        Assert.False(archiveLedger.Contains(Relative(busy), Big / 2));
        Assert.Equal(1, job.Status.Deferred);
        Assert.Equal(Big / 2, new FileInfo(free).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(busy)!, "*.convert.tmp"));
    }

    [Fact]
    public async Task Takes_the_newest_first_and_stops_at_the_limit()
    {
        var older = Write("2026-11-18", "A", "09-00-00", lastWriteUtc: Now.AddDays(-5));
        var newer = Write("2026-11-20", "A", "09-00-00", lastWriteUtc: Now.AddDays(-3));
        var job = Build();

        await job.RunAsync(dryRun: false, limit: 1, CancellationToken.None);

        Assert.Equal(Big / 2, new FileInfo(newer).Length);
        Assert.Equal(Big, new FileInfo(older).Length);
    }

    [Fact]
    public async Task Stop_lets_the_file_in_hand_finish_and_leaves_the_rest()
    {
        var paths = Enumerable.Range(1, 5).Select(day => Write($"2026-11-{10 + day:00}", "A", "09-00-00", lastWriteUtc: Now.AddDays(-day))).ToArray();
        RadioOpusConversionJob? job = null;
        job = Build(reencode: (input, output) =>
        {
            job!.Stop();
            HalveTheSize(input, output);
        });

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(1, job.Status.Converted);
        Assert.Equal(4, paths.Count(path => new FileInfo(path).Length == Big));
        Assert.Equal("finished", job.Status.State);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        Write("2026-11-20", "A", "09-00-00");
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var job = Build(reencode: (input, output) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            HalveTheSize(input, output);
        });

        Assert.True(job.TryStart(dryRun: false, limit: null));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("running", job.Status.State);
        Assert.False(job.TryStart(dryRun: false, limit: null));

        release.Set();
        await job.WaitUntilFinishedAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("finished", job.Status.State);
    }

    [Fact]
    public async Task Clears_the_temporary_files_an_earlier_interrupted_run_left_behind()
    {
        var path = Write("2026-11-20", "A", "09-00-00");
        var stale = path + ".convert.tmp";
        var fresh = Path.Combine(Path.GetDirectoryName(path)!, "A_2026-11-20_10-00-00.opus.convert.tmp");
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.WriteAllBytes(fresh, [1, 2, 3]);
        File.SetLastWriteTimeUtc(stale, Now.AddHours(-3));
        File.SetLastWriteTimeUtc(fresh, Now.AddMinutes(-2));

        await Build().RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    // Records how many files were being re-encoded at the same moment.
    private sealed class ConcurrencyProbe
    {
        private int running;
        public int Max { get; private set; }

        public void Encode(string input, string output)
        {
            var now = Interlocked.Increment(ref running);
            lock (this) Max = Math.Max(Max, now);
            Thread.Sleep(150);
            HalveTheSize(input, output);
            Interlocked.Decrement(ref running);
        }
    }

    [Fact]
    public async Task Converts_as_many_files_at_once_as_the_run_asks_for()
    {
        for (var i = 1; i <= 8; i++) Write($"2026-11-{10 + i:00}", "A", "09-00-00");
        var probe = new ConcurrencyProbe();
        var job = Build(reencode: probe.Encode, parallel: 1);

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None, parallel: 4);

        Assert.Equal(4, probe.Max);
        Assert.Equal(4, job.Status.Parallel);
        Assert.Equal(8, job.Status.Converted);
    }

    [Fact]
    public async Task Never_runs_more_than_sixteen_at_once_whatever_is_asked()
    {
        for (var i = 1; i <= 20; i++) Write($"2026-11-{i:00}", "A", "09-00-00");
        var probe = new ConcurrencyProbe();
        var job = Build(reencode: probe.Encode);

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None, parallel: 100);

        Assert.InRange(probe.Max, 2, 16);
        Assert.Equal(16, job.Status.Parallel);
    }

    [Fact]
    public async Task Uses_the_number_in_the_options_when_the_run_does_not_ask_for_one()
    {
        Write("2026-11-20", "A", "09-00-00");
        var job = Build(parallel: 3);

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(3, job.Status.Parallel);
    }

    [Fact]
    public async Task A_number_below_one_means_the_one_in_the_options()
    {
        Write("2026-11-20", "A", "09-00-00");
        var job = Build(parallel: 2);

        await job.RunAsync(dryRun: false, limit: null, CancellationToken.None, parallel: 0);

        Assert.Equal(2, job.Status.Parallel);
    }

    [Fact]
    public async Task The_status_shows_the_size_of_the_hours_the_capture_is_writing_now()
    {
        // A closed hour written with the new quality weighs about 14 MB: this is how to confirm it before converting.
        Write("2026-11-23", "A", "10-00-00", size: 14_400_000, lastWriteUtc: Now.AddHours(-2));
        Write("2026-11-23", "A", "11-00-00", size: 14_400_000, lastWriteUtc: Now.AddHours(-1));
        var job = Build();

        var average = job.Status.RecentClosedHoursAverageMb;

        Assert.NotNull(average);
        Assert.Equal(14.4, average.Value, precision: 1);
        await Task.CompletedTask;
    }
}
