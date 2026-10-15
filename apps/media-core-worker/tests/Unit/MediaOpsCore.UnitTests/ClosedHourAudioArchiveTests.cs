using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// An alert of a previous day has no .opus on this disk any more (the purge keeps only today's); its hour is
// in the archive as an mp3, which the reader fetches into a cache folder and cuts like any other source.
public sealed class ClosedHourAudioArchiveTests
{
    private static string SampleOpus => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-radio.opus");
    private static string SampleMp3 => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-radio.mp3");

    private static readonly CaptureSource RadioSource = new(
        sourceId: "radio-a", tenantId: "global-ingestion", platform: "test-platform", media: "radio",
        streamUrl: "https://example.com/stream", country: "colombia"); // -05:00

    private static readonly DateTimeOffset Hour8 = new(2026, 1, 15, 8, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset Hour9 = Hour8.AddHours(1);

    private static string ArchivedPath(DateTimeOffset hour, string extension = ".opus") =>
        $"radio/{hour:yyyy}/{hour:MM}/{hour:dd}/radio-a/radio-a_{hour:yyyy-MM-dd_HH-mm-ss}{extension}";

    private static string Mp3Path(DateTimeOffset hour) => ArchivedPath(hour, ".mp3");

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"closed-hour-archive-{Guid.NewGuid():N}");

    // An archive holding the mp3 sample at the given relative paths.
    private sealed class FakeArchive(params string[] relativePaths) : IArchiveStorage
    {
        public int Downloads { get; private set; }
        public int Listings { get; private set; }
        public bool ListThrows { get; set; }

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default)
        {
            Listings++;
            if (ListThrows) throw new IOException("server down");
            return Task.FromResult<IReadOnlyList<string>>(
                relativePaths.Where(path => path.StartsWith(relativeDirectory + "/", StringComparison.Ordinal))
                    .Select(path => path[(relativeDirectory.Length + 1)..]).ToList());
        }

        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default)
        {
            Downloads++;
            // Radio is archived as opus; the mp3 is what was uploaded before the format changed.
            File.Copy(relativePath.EndsWith(".mp3", StringComparison.Ordinal) ? SampleMp3 : SampleOpus, localPath, overwrite: true);
            return Task.FromResult(true);
        }
    }

    private static ClosedHourAudioSegmentReader Reader(string root, IArchiveStorage? archive) =>
        new(
            new FakeSources(RadioSource),
            new FakeResolver(),
            new NoLiveSessions(),
            new OperationsWorkerOptions
            {
                AudioOutputRootPath = Path.Combine(root, "audio"),
                StageFilesystemRootPath = Path.Combine(root, "stage"),
            },
            NullLogger<ClosedHourAudioSegmentReader>.Instance,
            archive);

    // 16 kHz mono 16-bit wav: 32 000 bytes a second after a 44-byte header.
    private static double WavSeconds(ClosedHourAudioResult result) =>
        (Assert.IsType<ClosedHourAudioSuccess>(result).Bytes.Length - 44) / 32000.0;

    [Fact]
    public async Task ExtractSegmentAsync_serves_a_previous_day_hour_from_the_archived_opus_and_downloads_it_only_once()
    {
        var root = NewRoot();
        try
        {
            var archive = new FakeArchive(ArchivedPath(Hour8));
            var reader = Reader(root, archive);

            var first = await reader.ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");
            var second = await reader.ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.InRange(WavSeconds(first), 1.8, 2.4);
            Assert.InRange(WavSeconds(second), 1.8, 2.4);
            Assert.Equal(1, archive.Downloads);
            Assert.Equal(1, archive.Listings); // the second request is served from the cache without touching the server
            Assert.True(File.Exists(Path.Combine(root, "stage", "audio-cache", ArchivedPath(Hour8))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_still_serves_an_hour_that_was_archived_as_mp3_before_the_format_changed()
    {
        var root = NewRoot();
        try
        {
            var archive = new FakeArchive(Mp3Path(Hour8));

            var result = await Reader(root, archive).ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.InRange(WavSeconds(result), 1.8, 2.4);
            Assert.True(File.Exists(Path.Combine(root, "stage", "audio-cache", Mp3Path(Hour8))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_prefers_the_opus_when_an_hour_is_archived_in_both_formats()
    {
        var root = NewRoot();
        try
        {
            var archive = new FakeArchive(Mp3Path(Hour8), ArchivedPath(Hour8));

            await Reader(root, archive).ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.True(File.Exists(Path.Combine(root, "stage", "audio-cache", ArchivedPath(Hour8))));
            Assert.False(File.Exists(Path.Combine(root, "stage", "audio-cache", Mp3Path(Hour8))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_does_not_touch_the_archive_when_the_opus_is_still_on_disk()
    {
        var root = NewRoot();
        try
        {
            var local = Path.Combine(root, "audio", "radio", "2026", "01", "15", "radio-a", "radio-a_2026-01-15_08-00-00.opus");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.Copy(SampleOpus, local);
            var archive = new FakeArchive(ArchivedPath(Hour8));

            var result = await Reader(root, archive).ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.InRange(WavSeconds(result), 1.8, 2.4);
            Assert.Equal(0, archive.Listings);
            Assert.Equal(0, archive.Downloads);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_joins_an_hour_on_disk_with_the_next_one_from_the_archive()
    {
        var root = NewRoot();
        try
        {
            var local = Path.Combine(root, "audio", "radio", "2026", "01", "15", "radio-a", "radio-a_2026-01-15_08-00-00.opus");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.Copy(SampleOpus, local);
            var archive = new FakeArchive(ArchivedPath(Hour9));

            // The window reaches 2 s into the second hour, so both files are read to their end: the sample files are
            // 3 s long (not an hour), which makes 3 s from the opus on disk plus 3 s from the archived opus.
            var result = await Reader(root, archive).ExtractSegmentAsync("radio-a", Hour8, Hour9.AddSeconds(2), 64, 16000, "wav");

            Assert.InRange(WavSeconds(result), 5.8, 6.3);
            Assert.Equal(1, archive.Downloads);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_says_there_is_no_archived_audio_when_the_archive_does_not_have_the_hour()
    {
        var root = NewRoot();
        try
        {
            var result = await Reader(root, new FakeArchive()).ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.Contains("archivado", Assert.IsType<ClosedHourAudioNotAvailable>(result).Reason);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_distinguishes_an_unreachable_archive_from_a_missing_hour()
    {
        var root = NewRoot();
        try
        {
            var result = await Reader(root, new FakeArchive(ArchivedPath(Hour8)) { ListThrows = true })
                .ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.Contains("consultar", Assert.IsType<ClosedHourAudioNotAvailable>(result).Reason);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_keeps_todays_behaviour_when_there_is_no_archive()
    {
        var root = NewRoot();
        try
        {
            var result = await Reader(root, archive: null).ExtractSegmentAsync("radio-a", Hour8, Hour8.AddSeconds(2), 64, 16000, "wav");

            Assert.Contains("No recording found", Assert.IsType<ClosedHourAudioNotAvailable>(result).Reason);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeSources(params CaptureSource[] sources) : ICaptureSourceRepository
    {
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureSource>>(sources);

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(CaptureSource source, IngestionMode ingestionMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginExecutionPlan(
                pluginId: "test-plugin",
                flacWindowDuration: TimeSpan.FromSeconds(5),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1)));
    }

    private sealed class NoLiveSessions : ILiveCaptureProgressReader
    {
        public IReadOnlyCollection<string> ActiveSourceIds => [];

        public IReadOnlyCollection<string> StoppedSourceIds => [];

        public LiveCaptureProgress? TryGetLiveProgress(string sourceId) => null;
    }
}
