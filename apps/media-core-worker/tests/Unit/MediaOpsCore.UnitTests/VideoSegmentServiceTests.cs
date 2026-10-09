using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class VideoSegmentServiceTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-tv.mp4");

    private sealed class FakeSources(params CaptureSource[] sources) : ICaptureSourceProvider
    {
        public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureSource>>(sources);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"video-seg-{Guid.NewGuid():N}");

    // Puts the 8 s sample where a real recording that started at `start` would be.
    private static void Record(string root, string sourceId, DateTimeOffset start)
    {
        var dir = Path.Combine(root, "television", start.ToString("yyyy"), start.ToString("MM"), start.ToString("dd"), sourceId);
        Directory.CreateDirectory(dir);
        File.Copy(Sample, Path.Combine(dir, $"{sourceId}_{start:yyyy-MM-dd_HH-mm-ss}.mp4"));
    }

    private static VideoSegmentService Service(string root) =>
        new(
            new OperationsWorkerOptions { AudioOutputRootPath = root },
            new FakeSources(new CaptureSource("rcn_tv", "youtube", "RCN", "television", "https://x/live")),
            NullLogger<VideoSegmentService>.Instance);

    [Fact]
    public async Task GetSegmentAsync_cuts_the_window_out_of_the_recording_that_covers_it()
    {
        var root = NewRoot();
        VideoSegmentSuccess? clip = null;
        try
        {
            var start = new DateTimeOffset(2026, 10, 5, 12, 5, 59, Bogota);
            Record(root, "rcn_tv", start);

            // 4.5 s to 7 s into the recording: the keyframe before 4.5 s is the one at 4.08 s.
            var result = await Service(root).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4.5).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);

            clip = Assert.IsType<VideoSegmentSuccess>(result);
            var info = Mp4Info.Read(clip.TempPath);
            Assert.True(info.HasVideo && info.HasAudio);
            Assert.InRange(info.DurationSeconds, 2.5, 3.5);
            // Stream copy starts on the keyframe at 4.08 s, so the clip's real start is reported in UTC.
            Assert.Equal(start.AddSeconds(4.08).ToUniversalTime(), clip.ClipStartUtc, TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            if (clip is not null) File.Delete(clip.TempPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_finds_a_recording_that_started_the_day_before()
    {
        var root = NewRoot();
        VideoSegmentSuccess? clip = null;
        try
        {
            // Started at 23:59:55: under a minute was left, so it runs through the next hour — the file
            // sits in the 4th's folder but holds the first minutes of the 5th.
            var start = new DateTimeOffset(2026, 10, 4, 23, 59, 55, Bogota);
            Record(root, "rcn_tv", start);

            var window = new DateTimeOffset(2026, 10, 5, 0, 0, 0, Bogota);
            var result = await Service(root).GetSegmentAsync(
                "rcn_tv", window.ToUniversalTime(), window.AddSeconds(3).ToUniversalTime(), CancellationToken.None);

            clip = Assert.IsType<VideoSegmentSuccess>(result);
        }
        finally
        {
            if (clip is not null) File.Delete(clip.TempPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_reports_not_available_when_no_recording_covers_the_instant()
    {
        var root = NewRoot();
        try
        {
            Record(root, "rcn_tv", new DateTimeOffset(2026, 10, 5, 12, 5, 59, Bogota));

            var later = new DateTimeOffset(2026, 10, 5, 15, 0, 0, Bogota);
            var result = await Service(root).GetSegmentAsync(
                "rcn_tv", later.ToUniversalTime(), later.AddMinutes(1).ToUniversalTime(), CancellationToken.None);

            Assert.IsType<VideoSegmentNotAvailable>(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_reports_not_available_when_the_window_is_past_what_was_actually_recorded()
    {
        var root = NewRoot();
        try
        {
            // The recording was planned to last until 13:00 but this one only holds 8 s (a stream that dropped).
            var start = new DateTimeOffset(2026, 10, 5, 12, 5, 59, Bogota);
            Record(root, "rcn_tv", start);

            var result = await Service(root).GetSegmentAsync(
                "rcn_tv", start.AddMinutes(20).ToUniversalTime(), start.AddMinutes(21).ToUniversalTime(), CancellationToken.None);

            Assert.IsType<VideoSegmentNotAvailable>(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_does_not_mix_up_sources_whose_ids_start_the_same()
    {
        var root = NewRoot();
        try
        {
            var start = new DateTimeOffset(2026, 10, 5, 12, 5, 59, Bogota);
            Record(root, "canal_10", start);

            var result = await Service(root).GetSegmentAsync(
                "canal_1", start.AddSeconds(2).ToUniversalTime(), start.AddSeconds(5).ToUniversalTime(), CancellationToken.None);

            Assert.IsType<VideoSegmentNotAvailable>(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // An archive holding the sample under the same relative layout as the local disk.
    private sealed class FakeArchive(params string[] relativePaths) : IArchiveStorage
    {
        public int Downloads { get; private set; }
        public bool DownloadSucceeds { get; set; } = true;
        public bool ListThrows { get; set; }

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default)
        {
            if (ListThrows) throw new IOException("server down");
            return Task.FromResult<IReadOnlyList<string>>(
                relativePaths.Where(path => path.StartsWith(relativeDirectory + "/")).Select(path => path[(relativeDirectory.Length + 1)..]).ToList());
        }

        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (DownloadSucceeds)
            {
                File.Copy(Sample, localPath, overwrite: true);
                // A real download is written now; File.Copy would keep the sample's old mtime and
                // the cache would see the file as already expired.
                File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow);
            }
            return Task.FromResult(DownloadSucceeds);
        }
    }

    private static VideoSegmentService ServiceWithArchive(string root, IArchiveStorage archive) =>
        new(
            new OperationsWorkerOptions { AudioOutputRootPath = Path.Combine(root, "audio"), StageFilesystemRootPath = Path.Combine(root, "stage") },
            new FakeSources(new CaptureSource("rcn_tv", "youtube", "RCN", "television", "https://x/live")),
            NullLogger<VideoSegmentService>.Instance,
            archive);

    private const string YesterdayFile = "television/2026/10/04/rcn_tv/rcn_tv_2026-10-04_12-05-59.mp4";

    [Fact]
    public async Task GetSegmentAsync_fetches_a_previous_day_recording_from_the_archive_once_and_reuses_the_cache()
    {
        var root = NewRoot();
        var archive = new FakeArchive(YesterdayFile);
        var start = new DateTimeOffset(2026, 10, 4, 12, 5, 59, Bogota);
        var clips = new List<string>();
        try
        {
            var service = ServiceWithArchive(root, archive);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await service.GetSegmentAsync(
                    "rcn_tv", start.AddSeconds(4.5).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);
                clips.Add(Assert.IsType<VideoSegmentSuccess>(result).TempPath);
            }

            Assert.Equal(1, archive.Downloads);
            Assert.True(File.Exists(Path.Combine(root, "stage", "video-cache", YesterdayFile)));
        }
        finally
        {
            clips.ForEach(File.Delete);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_prefers_the_local_recording_over_the_archive()
    {
        var root = NewRoot();
        var archive = new FakeArchive(YesterdayFile);
        var start = new DateTimeOffset(2026, 10, 4, 12, 5, 59, Bogota);
        VideoSegmentSuccess? clip = null;
        try
        {
            Record(Path.Combine(root, "audio"), "rcn_tv", start);

            var result = await ServiceWithArchive(root, archive).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4.5).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);

            clip = Assert.IsType<VideoSegmentSuccess>(result);
            Assert.Equal(0, archive.Downloads);
        }
        finally
        {
            if (clip is not null) File.Delete(clip.TempPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_says_so_when_the_archive_does_not_have_it_or_cannot_be_reached()
    {
        var root = NewRoot();
        var start = new DateTimeOffset(2026, 10, 4, 12, 5, 59, Bogota);
        try
        {
            var missing = await ServiceWithArchive(root, new FakeArchive()).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);
            Assert.Contains("No hay video grabado", Assert.IsType<VideoSegmentNotAvailable>(missing).Reason);

            var down = await ServiceWithArchive(root, new FakeArchive(YesterdayFile) { ListThrows = true }).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);
            Assert.Contains("consultar", Assert.IsType<VideoSegmentNotAvailable>(down).Reason);

            var failed = await ServiceWithArchive(root, new FakeArchive(YesterdayFile) { DownloadSucceeds = false }).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);
            Assert.Contains("descargar", Assert.IsType<VideoSegmentNotAvailable>(failed).Reason);
            Assert.False(File.Exists(Path.Combine(root, "stage", "video-cache", YesterdayFile + ".part")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSegmentAsync_trims_cached_recordings_past_their_age()
    {
        var root = NewRoot();
        var archive = new FakeArchive(YesterdayFile);
        var start = new DateTimeOffset(2026, 10, 4, 12, 5, 59, Bogota);
        VideoSegmentSuccess? clip = null;
        try
        {
            var old = Path.Combine(root, "stage", "video-cache", "television", "2026", "10", "01", "rcn_tv", "rcn_tv_2026-10-01_08-00-00.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.Copy(Sample, old);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

            var result = await ServiceWithArchive(root, archive).GetSegmentAsync(
                "rcn_tv", start.AddSeconds(4.5).ToUniversalTime(), start.AddSeconds(7).ToUniversalTime(), CancellationToken.None);

            clip = Assert.IsType<VideoSegmentSuccess>(result);
            Assert.False(File.Exists(old));
        }
        finally
        {
            if (clip is not null) File.Delete(clip.TempPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
