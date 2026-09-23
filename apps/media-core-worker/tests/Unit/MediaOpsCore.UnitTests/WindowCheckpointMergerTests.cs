using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class WindowCheckpointMergerTests
{
    [Fact]
    public void Merge_should_keep_the_later_snapshot_when_it_covers_the_window_from_its_start()
    {
        var earlier = new[] { new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(600, 600, 0) };
        var later = new[] { new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(900, 900, 0) };

        var merged = WindowCheckpointMerger.Merge(earlier, later);

        Assert.Equal(later, merged);
    }

    [Fact]
    public void Merge_should_rebase_a_resumed_snapshot_onto_the_earlier_history()
    {
        // Recorded 0-600s, worker restarted, new session bridged 600-660s then captured to 960s.
        // Its own counters restart at zero, so they have to be carried onto the earlier totals.
        var earlier = new[] { new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(600, 600, 0) };
        var later = new[] { new WindowCheckpoint(660, 0, 60), new WindowCheckpoint(960, 300, 60) };

        var merged = WindowCheckpointMerger.Merge(earlier, later);

        Assert.Equal(
            [
                new WindowCheckpoint(0, 0, 0),
                new WindowCheckpoint(600, 600, 0),
                new WindowCheckpoint(660, 600, 60),
                new WindowCheckpoint(960, 900, 60),
            ],
            merged);
    }

    [Fact]
    public void Merged_checkpoints_should_read_as_recorded_then_down_then_recording_again()
    {
        var earlier = new[] { new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(600, 600, 0) };
        var later = new[] { new WindowCheckpoint(660, 0, 60), new WindowCheckpoint(960, 300, 60) };

        var segments = CaptureSegmentBuilder.Build(WindowCheckpointMerger.Merge(earlier, later), 960);

        Assert.Equal(
            [
                new CaptureSegment(0, 600, "captured"),
                new CaptureSegment(600, 660, "no-session"),
                new CaptureSegment(660, 960, "captured"),
            ],
            segments);
    }

    [Fact]
    public void Merge_should_return_whichever_side_has_data_when_the_other_is_empty()
    {
        var checkpoints = new[] { new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(600, 600, 0) };

        Assert.Equal(checkpoints, WindowCheckpointMerger.Merge(null, checkpoints));
        Assert.Equal(checkpoints, WindowCheckpointMerger.Merge(checkpoints, []));
        Assert.Empty(WindowCheckpointMerger.Merge(null, null));
    }

    [Fact]
    public void Merge_should_carry_over_nothing_when_the_earlier_history_starts_after_the_splice()
    {
        // Nothing in the earlier set precedes where the later one resumed, so there are no totals
        // to carry — the later snapshot stands on its own offsets.
        var earlier = new[] { new WindowCheckpoint(900, 900, 0) };
        var later = new[] { new WindowCheckpoint(300, 0, 60), new WindowCheckpoint(600, 240, 60) };

        var merged = WindowCheckpointMerger.Merge(earlier, later);

        Assert.Equal(
            [
                new WindowCheckpoint(300, 0, 60),
                new WindowCheckpoint(600, 240, 60),
            ],
            merged);
    }
}
