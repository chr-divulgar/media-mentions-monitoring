using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class CaptureClockAnchorTests
{
    private const int Rate = 16000;
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 16, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset HourStart = new(2026, 9, 23, 10, 0, 0, TimeSpan.FromHours(-5));

    private static long S(double seconds) => (long)(seconds * Rate);

    [Fact]
    public void Burst_shorter_than_the_gap_shrinks_the_silence_bridge()
    {
        // Fresh file at 10:16 with a 40s burst: audio is 10:15:20-10:16:00, so silence covers 10:00-10:15:20.
        var placement = InProcessFfmpegAudioCapturePlugin.PlaceStartupBurst(HourStart, Now, S(40), previousHourGap: null);

        Assert.Equal(S(920), placement.BridgeSamples);
        Assert.Equal(0, placement.DroppedSamples);
        Assert.Equal(0, placement.PreviousHourSamples);
        Assert.Equal(S(40), placement.KeptSamples);
    }

    [Fact]
    public void Burst_longer_than_the_gap_spills_into_the_previous_hour_gap()
    {
        // Session starts at 10:00:10 with a 40s burst: 10s fill this hour, 30s go to the end of 09:00.
        var now = HourStart.AddSeconds(10);
        var placement = InProcessFfmpegAudioCapturePlugin.PlaceStartupBurst(HourStart, now, S(40), previousHourGap: TimeSpan.FromMinutes(2));

        Assert.Equal(0, placement.BridgeSamples);
        Assert.Equal(0, placement.DroppedSamples);
        Assert.Equal(S(30), placement.PreviousHourSamples);
        Assert.Equal(S(10), placement.KeptSamples);
    }

    [Fact]
    public void Spill_beyond_the_previous_hour_gap_is_already_recorded_audio_and_is_dropped()
    {
        var now = HourStart.AddSeconds(10);
        var placement = InProcessFfmpegAudioCapturePlugin.PlaceStartupBurst(HourStart, now, S(40), previousHourGap: TimeSpan.FromSeconds(5));

        Assert.Equal(S(25), placement.DroppedSamples);
        Assert.Equal(S(5), placement.PreviousHourSamples);
        Assert.Equal(S(10), placement.KeptSamples);
    }

    [Fact]
    public void Spill_with_no_previous_hour_is_before_the_first_recording_and_is_dropped()
    {
        var now = HourStart.AddSeconds(10);
        var placement = InProcessFfmpegAudioCapturePlugin.PlaceStartupBurst(HourStart, now, S(40), previousHourGap: null);

        Assert.Equal(S(30), placement.DroppedSamples);
        Assert.Equal(S(10), placement.KeptSamples);
    }

    [Fact]
    public void Resumed_file_already_ahead_of_the_clock_keeps_nothing_new()
    {
        var fileEndsAt = Now.AddMinutes(7);
        var placement = InProcessFfmpegAudioCapturePlugin.PlaceStartupBurst(fileEndsAt, Now, S(40), previousHourGap: null);

        Assert.Equal(0, placement.BridgeSamples);
        Assert.Equal(S(40), placement.DroppedSamples);
        Assert.Equal(0, placement.KeptSamples);
    }

    [Fact]
    public void Frame_within_the_clock_has_no_surplus()
    {
        Assert.Equal(0, InProcessFfmpegAudioCapturePlugin.ResolveSurplusSamples(Now, Now, 320));
        Assert.Equal(0, InProcessFfmpegAudioCapturePlugin.ResolveSurplusSamples(Now.AddSeconds(-1), Now, 320));
    }

    [Fact]
    public void Frame_ending_ahead_of_the_clock_drops_only_the_part_beyond_now()
    {
        Assert.Equal(160, InProcessFfmpegAudioCapturePlugin.ResolveSurplusSamples(Now.AddMilliseconds(10), Now, 320));
        Assert.Equal(320, InProcessFfmpegAudioCapturePlugin.ResolveSurplusSamples(Now.AddMinutes(6), Now, 320));
    }
}
