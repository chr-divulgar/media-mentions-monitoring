using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class CaptureSegmentBuilderTests
{
    [Fact]
    public void Build_should_return_a_single_no_session_segment_when_there_are_no_checkpoints()
    {
        var segments = CaptureSegmentBuilder.Build([], 3600);

        var segment = Assert.Single(segments);
        Assert.Equal(0, segment.StartSeconds);
        Assert.Equal(3600, segment.EndSeconds);
        Assert.Equal("no-session", segment.State);
    }

    [Fact]
    public void Build_should_add_a_leading_no_session_segment_when_the_first_checkpoint_is_mid_hour()
    {
        // Worker started 50 minutes into the hour (elapsed=3000) — the first checkpoint anchors
        // that as the session's true start offset.
        var checkpoints = new[]
        {
            new WindowCheckpoint(3000, 0, 0),
            new WindowCheckpoint(3600, 600, 0),
        };

        var segments = CaptureSegmentBuilder.Build(checkpoints, 3600);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new CaptureSegment(0, 3000, "no-session"), segments[0]);
        Assert.Equal(new CaptureSegment(3000, 3600, "captured"), segments[1]);
    }

    [Fact]
    public void Build_should_mark_a_bridged_slice_as_no_session_rather_than_a_gap()
    {
        // Silence-filled seconds have exactly one source in the capture session: the bridge it
        // writes at startup to cover a span it wasn't running for. So a slice that is almost all
        // bridge silence means the worker was down for it, not that capture degraded.
        var checkpoints = new[]
        {
            new WindowCheckpoint(0, 0, 0),
            new WindowCheckpoint(1800, 1800, 0), // first half: fully captured
            new WindowCheckpoint(3600, 1810, 1790), // second half: bridged silence
        };

        var segments = CaptureSegmentBuilder.Build(checkpoints, 3600);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new CaptureSegment(0, 1800, "captured"), segments[0]);
        Assert.Equal(new CaptureSegment(1800, 3600, "no-session"), segments[1]);
    }

    [Fact]
    public void Build_should_mark_a_slice_as_gap_when_neither_real_audio_nor_bridge_accounts_for_it()
    {
        // The recording advanced but little of it is real audio and none of it was bridged —
        // partial coverage that is genuinely degraded, which is what "gap" is for.
        var checkpoints = new[]
        {
            new WindowCheckpoint(0, 0, 0),
            new WindowCheckpoint(1800, 900, 0),
        };

        var segments = CaptureSegmentBuilder.Build(checkpoints, 1800);

        var segment = Assert.Single(segments);
        Assert.Equal(new CaptureSegment(0, 1800, "gap"), segment);
    }

    [Fact]
    public void Build_should_report_no_session_past_where_the_recording_stopped()
    {
        // The recording reached 1800s of a 3600s window and stopped there.
        var checkpoints = new[]
        {
            new WindowCheckpoint(0, 0, 0),
            new WindowCheckpoint(1800, 1800, 0),
        };

        var segments = CaptureSegmentBuilder.Build(checkpoints, 3600);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new CaptureSegment(0, 1800, "captured"), segments[0]);
        // The half hour the recording never reached is unknown, not more of the same.
        Assert.Equal(new CaptureSegment(1800, 3600, "no-session"), segments[1]);
    }

    [Fact]
    public void Build_should_ignore_checkpoints_past_the_elapsed_window()
    {
        // A sample taken right after a rotation carries an offset from the window that just closed;
        // pairing it with a real one used to paint a segment across time that hasn't happened yet.
        var segments = CaptureSegmentBuilder.Build(
            [
                new WindowCheckpoint(0, 0, 0),
                new WindowCheckpoint(300, 300, 0),
                new WindowCheckpoint(3598, 3598, 0),
            ],
            windowEndSeconds: 600);

        Assert.All(segments, segment => Assert.True(segment.EndSeconds <= 600));
    }

}
