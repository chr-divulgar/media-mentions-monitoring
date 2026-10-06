using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// TestData/sample-tv.mp4: 8 s, 160x90 H.264 + AAC, fragmented like the worker's own recordings, with a
// video keyframe every 2 s (at 0.08, 2.08, 4.08 and 6.08 s).
public sealed class Mp4SegmentCutterTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-tv.mp4");

    private static string TempClip() => Path.Combine(Path.GetTempPath(), $"clip-{Guid.NewGuid():N}.mp4");

    [Fact]
    public void Cut_starts_at_the_keyframe_before_the_requested_start_and_ends_where_asked()
    {
        var output = TempClip();
        try
        {
            var result = Mp4SegmentCutter.Cut(Sample, startSeconds: 3.0, endSeconds: 6.5, output, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2.08, result!.StartSeconds, precision: 1); // keyframe at 2.08 s, not 3.0
            Assert.Equal(4.42, result.DurationSeconds, precision: 0);

            var info = Mp4Info.Read(output);
            Assert.True(info.HasVideo);
            Assert.True(info.HasAudio);
            Assert.True(info.FirstVideoPacketIsKeyframe);
            Assert.InRange(info.DurationSeconds, 4.0, 5.0);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Cut_starting_exactly_on_a_keyframe_keeps_that_keyframe()
    {
        var output = TempClip();
        try
        {
            var result = Mp4SegmentCutter.Cut(Sample, startSeconds: 4.08, endSeconds: 6.0, output, CancellationToken.None);

            Assert.Equal(4.08, result!.StartSeconds, precision: 1);
            Assert.True(Mp4Info.Read(output).FirstVideoPacketIsKeyframe);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Cut_clamps_an_end_past_the_recording_to_the_end_of_the_file()
    {
        var output = TempClip();
        try
        {
            var result = Mp4SegmentCutter.Cut(Sample, startSeconds: 0, endSeconds: 600, output, CancellationToken.None);

            Assert.NotNull(result);
            Assert.InRange(Mp4Info.Read(output).DurationSeconds, 7.5, 8.5);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Cut_returns_nothing_when_the_requested_start_is_beyond_the_recording()
    {
        var output = TempClip();
        try
        {
            Assert.Null(Mp4SegmentCutter.Cut(Sample, startSeconds: 60, endSeconds: 90, output, CancellationToken.None));
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Cut_writes_a_clip_a_browser_can_seek_with_the_index_at_the_front()
    {
        var output = TempClip();
        try
        {
            Mp4SegmentCutter.Cut(Sample, startSeconds: 0, endSeconds: 4, output, CancellationToken.None);

            // +faststart puts the moov atom before the media data, so playback starts without
            // downloading the whole file first.
            var bytes = File.ReadAllBytes(output);
            var moov = IndexOf(bytes, "moov"u8);
            var mdat = IndexOf(bytes, "mdat"u8);
            Assert.True(moov > 0 && mdat > 0 && moov < mdat, $"moov at {moov}, mdat at {mdat}");
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
