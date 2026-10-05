using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class TvVideoRecordingTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    [Fact]
    public void Plan_writes_next_to_the_audio_and_records_until_the_next_hour()
    {
        var now = new DateTimeOffset(2026, 10, 4, 14, 20, 30, Bogota);

        var plan = TvVideoRecording.Plan(@"D:\", "television", "noticias-caracol-live", now);

        Assert.Equal(
            Path.Combine(@"D:\", "television", "2026", "10", "04", "noticias-caracol-live", "noticias-caracol-live_2026-10-04_14-20-30.mp4"),
            plan.OutputPath);
        Assert.Equal(TimeSpan.FromSeconds(39 * 60 + 30), plan.Duration);
    }

    [Fact]
    public void Plan_on_an_hour_boundary_records_the_full_hour()
    {
        var plan = TvVideoRecording.Plan(@"D:\", "television", "canal", new DateTimeOffset(2026, 10, 4, 15, 0, 0, Bogota));

        Assert.Equal(TimeSpan.FromHours(1), plan.Duration);
        Assert.EndsWith("canal_2026-10-04_15-00-00.mp4", plan.OutputPath);
    }

    [Fact]
    public void Plan_never_returns_a_uselessly_short_recording_right_before_the_hour()
    {
        // 14:59:55 → 5 s left: not worth a file; roll straight into the next full hour instead.
        var plan = TvVideoRecording.Plan(@"D:\", "television", "canal", new DateTimeOffset(2026, 10, 4, 14, 59, 55, Bogota));

        Assert.Equal(TimeSpan.FromSeconds(3605), plan.Duration);
    }

    [Fact]
    public void YtdlpVideoFormat_caps_the_resolution_to_bound_disk_use()
    {
        Assert.Equal("bestvideo[height<=480]+bestaudio/best[height<=480]/best", TvVideoRecording.YtdlpVideoFormat(480));
    }
}
