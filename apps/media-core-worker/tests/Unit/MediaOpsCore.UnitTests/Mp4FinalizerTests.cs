using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The hour being recorded is a fragmented MP4 (it survives a crash), but its header says it lasts about a second, so
// players and browsers will not let anyone move through it. Once the hour is closed it is turned into a regular MP4.
public sealed class Mp4FinalizerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"finalizer-{Guid.NewGuid():N}");

    public Mp4FinalizerTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Fragmented(string name = "rcn_tv_2026-10-14_20-00-00.mp4")
    {
        var path = Path.Combine(folder, name);
        InProcessMp4Recorder.Record(
            [Path.Combine(AppContext.BaseDirectory, "TestData", "sample-tv-stereo.mp4")], TimeSpan.FromHours(1), path,
            CancellationToken.None, new TvAudioSettings(32, 16000));
        return path;
    }

    [Fact]
    public void A_recording_is_fragmented_until_it_is_finalized()
    {
        var path = Fragmented();

        Assert.True(Mp4Finalizer.IsFragmented(path));
        Assert.True(Mp4Finalizer.Finalize(path, CancellationToken.None));
        Assert.False(Mp4Finalizer.IsFragmented(path));
    }

    [Fact]
    public void Finalize_keeps_the_picture_the_sound_and_their_length()
    {
        var path = Fragmented();
        var before = Mp4Info.Read(path, scanStreams: true);

        Mp4Finalizer.Finalize(path, CancellationToken.None);

        var after = Mp4Info.Read(path, scanStreams: true);
        Assert.True(after.HasVideo && after.HasAudio);
        Assert.Equal((before.VideoWidth, before.VideoHeight, before.AudioCodec, before.AudioChannels), (after.VideoWidth, after.VideoHeight, after.AudioCodec, after.AudioChannels));
        Assert.InRange(after.VideoDurationSeconds, before.VideoDurationSeconds - 0.05, before.VideoDurationSeconds + 0.05);
        Assert.InRange(after.AudioDurationSeconds, before.AudioDurationSeconds - 0.05, before.AudioDurationSeconds + 0.05);
        Assert.InRange(after.AudioStartSeconds, before.AudioStartSeconds - 0.05, before.AudioStartSeconds + 0.05);
    }

    [Fact]
    public void Finalize_puts_the_real_duration_in_the_header_and_leaves_nothing_else_behind()
    {
        var path = Fragmented();

        Mp4Finalizer.Finalize(path, CancellationToken.None);

        // The header (not a scan of the packets) says how long it is: that is what a player reads to allow seeking.
        Assert.InRange(Mp4Info.Read(path).DurationSeconds, 5.5, 6.5);
        Assert.Equal([path], Directory.GetFiles(folder));
    }

    [Fact]
    public void Finalize_leaves_a_regular_mp4_alone()
    {
        var path = Fragmented();
        Mp4Finalizer.Finalize(path, CancellationToken.None);
        var written = File.GetLastWriteTimeUtc(path);
        var size = new FileInfo(path).Length;

        Assert.False(Mp4Finalizer.Finalize(path, CancellationToken.None));

        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Finalize_leaves_a_clip_cut_from_it_working()
    {
        var path = Fragmented();
        Mp4Finalizer.Finalize(path, CancellationToken.None);
        var clip = Path.Combine(folder, "clip.mp4");

        var result = Mp4SegmentCutter.Cut(path, 2.5, 5.0, clip, CancellationToken.None);

        Assert.NotNull(result);
        Assert.InRange(Mp4Info.Read(clip).DurationSeconds, 2.5, 3.5);
    }

    [Fact]
    public void Finalize_leaves_the_original_untouched_when_it_cannot_be_read()
    {
        var path = Path.Combine(folder, "broken.mp4");
        File.WriteAllBytes(path, [0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e']);
        var before = File.ReadAllBytes(path);

        Assert.False(Mp4Finalizer.Finalize(path, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(folder));
    }
}
