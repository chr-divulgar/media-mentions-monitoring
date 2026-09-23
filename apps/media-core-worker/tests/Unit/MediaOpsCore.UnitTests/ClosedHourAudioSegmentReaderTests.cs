using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Covers ClosedHourAudioSegmentReader's routing/decision logic (which of the four
// ClosedHourAudioResult cases a request lands on) with fakes for its three dependencies. The
// actual FFmpeg decode/cut/encode pipeline (OpusSegmentTranscoder) is exercised here only through
// its failure path — its success path was verified manually against real production .opus files
// (matched a system-ffmpeg CLI extraction of the same window almost exactly: same duration, same
// start offset, mean/max volume within 1dB) rather than a synthetic fixture, since generating a
// valid Ogg Opus file just to re-prove what real-file testing already proved more convincingly
// isn't worth the fixture-generation machinery it would take.
public sealed class ClosedHourAudioSegmentReaderTests
{
    private static readonly CaptureSource RadioSource = new(
        sourceId: "radio-a",
        tenantId: "global-ingestion",
        platform: "test-platform",
        media: "radio",
        streamUrl: "https://example.com/stream",
        country: "colombia"); // -05:00, matches the worker's real deployment

    [Fact]
    public async Task ExtractSegmentAsync_should_return_source_not_found_for_an_unknown_sourceId()
    {
        var reader = CreateReader(sources: []);

        var result = await reader.ExtractSegmentAsync(
            "unknown-source", DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-2),
            16, 8000, "mp3");

        Assert.IsType<ClosedHourAudioSourceNotFound>(result);
    }

    [Fact]
    public async Task ExtractSegmentAsync_should_fall_back_to_still_recording_when_the_live_file_does_not_exist_yet()
    {
        // A session can be active (has live progress) before its first flush has put anything on
        // disk — TrySnapshotLiveFile has nothing to copy, so this falls back to the pre-snapshot
        // behavior rather than a misleading "not available".
        var liveProgress = new FakeLiveCaptureProgressReader(recordedSecondsBySource: new()
        {
            ["radio-a"] = 742.5,
        });
        var reader = CreateReader(sources: [RadioSource], liveCaptureProgressReader: liveProgress);

        // A window that starts and ends at "now" is guaranteed to sit inside the same hour "now"
        // itself is in, whatever minute the test happens to run at — unlike a 10-minute lookback,
        // which could straddle back into a previous, closed hour this test isn't set up to serve.
        var now = DateTimeOffset.UtcNow;
        var result = await reader.ExtractSegmentAsync(
            "radio-a", now.AddSeconds(-1), now, 16, 8000, "mp3");

        var stillRecording = Assert.IsType<ClosedHourAudioStillRecording>(result);
        Assert.Equal(742.5, stillRecording.RecordedSeconds);
    }

    [Fact]
    public async Task ExtractSegmentAsync_should_report_zero_recorded_seconds_when_no_session_is_active()
    {
        var reader = CreateReader(
            sources: [RadioSource], liveCaptureProgressReader: new FakeLiveCaptureProgressReader(recordedSecondsBySource: new()));

        var now = DateTimeOffset.UtcNow;
        var result = await reader.ExtractSegmentAsync("radio-a", now.AddMinutes(-5), now, 16, 8000, "mp3");

        var stillRecording = Assert.IsType<ClosedHourAudioStillRecording>(result);
        Assert.Equal(0, stillRecording.RecordedSeconds);
    }

    [Fact]
    public async Task ExtractSegmentAsync_should_report_not_available_when_no_file_exists_for_the_window()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var reader = CreateReader(sources: [RadioSource], audioOutputRootPath: tempRoot);

            // Comfortably in the past, and nothing was ever written to tempRoot for this source.
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            var result = await reader.ExtractSegmentAsync(
                "radio-a", start, start.AddMinutes(30), 16, 8000, "mp3");

            Assert.IsType<ClosedHourAudioNotAvailable>(result);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_should_resolve_the_exact_on_disk_path_a_capture_session_would_use()
    {
        // If the path convention (root/media/yyyy/MM/dd/sourceId/sourceId_yyyy-MM-dd_HH-mm-ss.opus)
        // is right, the reader finds this placeholder and attempts extraction — surfacing the
        // transcoder's decode failure (garbage bytes, not a real Ogg stream) as NotAvailable rather
        // than "no file found". That distinction is what proves the path was actually resolved,
        // not just defaulted to "nothing exists".
        var tempRoot = CreateTempDirectory();
        try
        {
            var hourStart = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.FromHours(-5));
            var expectedPath = Path.Combine(tempRoot, "radio", "2026", "01", "15", "radio-a", "radio-a_2026-01-15_08-00-00.opus");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            await File.WriteAllBytesAsync(expectedPath, "not a real opus file"u8.ToArray());

            var reader = CreateReader(sources: [RadioSource], audioOutputRootPath: tempRoot);

            var result = await reader.ExtractSegmentAsync(
                "radio-a", hourStart.AddMinutes(5), hourStart.AddMinutes(6), 16, 8000, "mp3");

            var notAvailable = Assert.IsType<ClosedHourAudioNotAvailable>(result);
            Assert.Contains("radio-a", notAvailable.Reason);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractSegmentAsync_should_snapshot_and_attempt_extraction_when_the_live_file_exists()
    {
        // A placeholder at the live hour's path proves TrySnapshotLiveFile found and copied it —
        // reaching (and failing on) the transcoder, rather than reporting "still recording", is
        // what shows the snapshot path was actually taken instead of the pre-snapshot fallback.
        // The reader has no injectable clock, so this has to use the real current hour rather than
        // a fixed one.
        var tempRoot = CreateTempDirectory();
        try
        {
            var nowInSourceOffset = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5));
            var currentHourStart = new DateTimeOffset(
                nowInSourceOffset.Year, nowInSourceOffset.Month, nowInSourceOffset.Day, nowInSourceOffset.Hour, 0, 0,
                nowInSourceOffset.Offset);
            var livePath = Path.Combine(
                tempRoot, "radio",
                currentHourStart.ToString("yyyy"), currentHourStart.ToString("MM"), currentHourStart.ToString("dd"),
                "radio-a", $"radio-a_{currentHourStart:yyyy-MM-dd_HH-mm-ss}.opus");
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            await File.WriteAllBytesAsync(livePath, "not a real opus file"u8.ToArray());

            var liveProgress = new FakeLiveCaptureProgressReader(recordedSecondsBySource: new() { ["radio-a"] = 300 });
            var reader = CreateReader(sources: [RadioSource], liveCaptureProgressReader: liveProgress, audioOutputRootPath: tempRoot);

            var result = await reader.ExtractSegmentAsync(
                "radio-a", currentHourStart.AddMinutes(1), DateTimeOffset.UtcNow, 16, 8000, "mp3");

            Assert.IsType<ClosedHourAudioNotAvailable>(result);
            // The snapshot copy is cleaned up regardless of what extraction did with it.
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "live-audio-snapshot-*.opus"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ClosedHourAudioSegmentReader CreateReader(
        IReadOnlyList<CaptureSource> sources,
        FakeLiveCaptureProgressReader? liveCaptureProgressReader = null,
        string? audioOutputRootPath = null)
    {
        return new ClosedHourAudioSegmentReader(
            new FakeCaptureSourceRepository(sources),
            new FakePluginResolver(),
            liveCaptureProgressReader ?? new FakeLiveCaptureProgressReader(recordedSecondsBySource: new()),
            new OperationsWorkerOptions { AudioOutputRootPath = audioOutputRootPath ?? "." },
            NullLogger<ClosedHourAudioSegmentReader>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"closed-hour-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeCaptureSourceRepository(IReadOnlyList<CaptureSource> sources) : ICaptureSourceRepository
    {
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakePluginResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(
            CaptureSource source, IngestionMode ingestionMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginExecutionPlan(
                pluginId: "test-plugin",
                flacWindowDuration: TimeSpan.FromSeconds(5),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1)));
    }

    private sealed class FakeLiveCaptureProgressReader(Dictionary<string, double> recordedSecondsBySource) : ILiveCaptureProgressReader
    {
        public IReadOnlyCollection<string> ActiveSourceIds => recordedSecondsBySource.Keys;

        public LiveCaptureProgress? TryGetLiveProgress(string sourceId) =>
            recordedSecondsBySource.TryGetValue(sourceId, out var recordedSeconds)
                ? new LiveCaptureProgress(
                    DateTimeOffset.UtcNow, recordedSeconds, [new WindowCheckpoint(recordedSeconds, recordedSeconds, 0)])
                : null;
    }
}
