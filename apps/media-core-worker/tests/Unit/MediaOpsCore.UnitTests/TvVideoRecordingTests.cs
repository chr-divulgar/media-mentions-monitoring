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
    public void FfmpegArguments_copy_a_single_stream_without_reencoding_into_a_crash_safe_mp4()
    {
        var args = TvVideoRecording.FfmpegArguments(["https://example/live.m3u8"], TimeSpan.FromSeconds(90), @"D:\out.mp4").ToList();

        Assert.Equal("https://example/live.m3u8", args[args.IndexOf("-i") + 1]);
        Assert.Equal("90", args[args.IndexOf("-t") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.Equal("aac_adtstoasc", args[args.IndexOf("-bsf:a") + 1]);
        Assert.DoesNotContain("-map", args);
        Assert.Contains(args, a => a.Contains("frag_keyframe"));
        Assert.Equal(@"D:\out.mp4", args[^1]);
    }

    [Fact]
    public void FfmpegArguments_mux_separate_video_and_audio_streams()
    {
        var args = TvVideoRecording.FfmpegArguments(["https://v", "https://a"], TimeSpan.FromSeconds(60), @"D:\out.mp4").ToList();

        Assert.Equal(2, args.Count(a => a == "-i"));
        Assert.Contains("0:v:0", args);
        Assert.Contains("1:a:0", args);
    }

    [Fact]
    public void YtdlpVideoFormat_caps_the_resolution_to_bound_disk_use()
    {
        Assert.Equal("bestvideo[height<=480]+bestaudio/best[height<=480]/best", TvVideoRecording.YtdlpVideoFormat(480));
    }
}
