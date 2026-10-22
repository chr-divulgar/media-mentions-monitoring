using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The clip a TV note keeps must start and end on the instants the user chose. Copying packets can only begin at the
// keyframe before the start (up to two seconds early on YouTube's streams), so the exact mode re-encodes the video.
public sealed class Mp4SegmentCutterExactTests
{
    // 6 s, 426x240 H.264 with a keyframe every 2 s, AAC stereo 44.1 kHz.
    private static string Source => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-tv-stereo.mp4");

    private static string NewOutput() => Path.Combine(Path.GetTempPath(), $"exact-{Guid.NewGuid():N}.mp4");

    [Fact]
    public void Copy_mode_starts_on_the_keyframe_before_the_asked_start()
    {
        var output = NewOutput();
        try
        {
            var clip = Mp4SegmentCutter.Cut(Source, 2.5, 5.0, output, CancellationToken.None);

            Assert.InRange(clip!.StartSeconds, 2.0, 2.25); // a third of a second early (the sample's keyframe): the reason for the exact mode
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Exact_mode_starts_and_ends_on_the_asked_instants()
    {
        var output = NewOutput();
        try
        {
            var clip = Mp4SegmentCutter.Cut(Source, 2.5, 5.0, output, CancellationToken.None, exact: true);

            Assert.NotNull(clip);
            Assert.Equal(2.5, clip!.StartSeconds, 0.01);
            Assert.InRange(clip.DurationSeconds, 2.4, 2.6);

            var info = Mp4Info.Read(output, scanStreams: true);
            Assert.True(info.HasVideo && info.HasAudio);
            Assert.Equal((426, 240), (info.VideoWidth, info.VideoHeight));
            Assert.True(info.FirstVideoPacketIsKeyframe); // it opens on its own, not mid-GOP
            Assert.InRange(info.VideoStartSeconds, 0, 0.08);          // the first picture is the one at 2.5 s
            Assert.InRange(info.VideoDurationSeconds, 2.4, 2.6);      // and the last ends at 5.0 s
            Assert.InRange(info.AudioStartSeconds, 0, 0.1);           // sound and picture begin together
            Assert.InRange(info.AudioStartSeconds + info.AudioDurationSeconds, 2.4, 2.65);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Exact_mode_on_a_keyframe_start_keeps_the_start_and_the_length()
    {
        var output = NewOutput();
        try
        {
            var clip = Mp4SegmentCutter.Cut(Source, 2.0, 4.0, output, CancellationToken.None, exact: true);

            Assert.Equal(2.0, clip!.StartSeconds, 0.01);
            Assert.InRange(Mp4Info.Read(output, scanStreams: true).VideoDurationSeconds, 1.9, 2.1);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Exact_mode_still_returns_nothing_for_a_start_past_the_recording()
    {
        var output = NewOutput();
        try
        {
            Assert.Null(Mp4SegmentCutter.Cut(Source, 60, 70, output, CancellationToken.None, exact: true));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void An_exact_clip_is_a_regular_mp4_that_can_be_cut_again()
    {
        var first = NewOutput();
        var second = NewOutput();
        try
        {
            Mp4SegmentCutter.Cut(Source, 1.2, 5.3, first, CancellationToken.None, exact: true);

            var again = Mp4SegmentCutter.Cut(first, 1.0, 2.0, second, CancellationToken.None, exact: true);

            Assert.NotNull(again);
            Assert.InRange(Mp4Info.Read(second, scanStreams: true).VideoDurationSeconds, 0.9, 1.1);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
