using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// A window that crosses from one hourly recording into the next is cut from each and joined into one MP4, with every
// clip at its real place in time (a hole between two recordings stays a hole, so nothing after it moves).
public sealed class Mp4JoinerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"joiner-{Guid.NewGuid():N}");

    public Mp4JoinerTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private string Output() => Path.Combine(folder, "joined.mp4");

    [Fact]
    public void Join_places_each_clip_at_its_own_time_and_keeps_picture_and_sound_in_step()
    {
        var clip = Sample("sample-tv.mp4"); // 8 s of video and sound
        var length = Mp4Info.Read(clip, scanStreams: true).VideoDurationSeconds;

        // The second clip starts 3 s after the first one ends.
        Assert.True(Mp4Joiner.TryJoin([(clip, 0.0), (clip, length + 3.0)], Output()));

        var info = Mp4Info.Read(Output(), scanStreams: true);
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.InRange(info.VideoStartSeconds + info.VideoDurationSeconds, 2 * length + 3 - 0.2, 2 * length + 3 + 0.2);
        Assert.InRange(info.AudioStartSeconds + info.AudioDurationSeconds, 2 * length + 3 - 0.4, 2 * length + 3 + 0.4);
        Assert.InRange(Math.Abs(info.AudioStartSeconds - info.VideoStartSeconds), 0, 0.15);
    }

    [Fact]
    public void Join_of_clips_back_to_back_adds_up_and_has_the_index_up_front()
    {
        var clip = Sample("sample-tv.mp4");
        var length = Mp4Info.Read(clip, scanStreams: true).VideoDurationSeconds;

        Assert.True(Mp4Joiner.TryJoin([(clip, 0.0), (clip, length)], Output()));

        // The header says so, so it can be sought.
        Assert.InRange(Mp4Info.Read(Output()).DurationSeconds, 2 * length - 0.2, 2 * length + 0.2);
        Assert.False(Mp4Finalizer.IsFragmented(Output()));
    }

    [Fact]
    public void Join_refuses_clips_that_do_not_match_in_picture_size_and_leaves_no_file()
    {
        // 160x90 and 426x240: one decoder setup cannot play both (the day the recording went from 240p to 480p).
        var small = Sample("sample-tv.mp4");
        var big = Sample("sample-tv-stereo.mp4");

        Assert.False(Mp4Joiner.TryJoin([(small, 0.0), (big, 10.0)], Output()));
        Assert.False(File.Exists(Output()));
    }

    [Fact]
    public void Join_of_a_single_clip_is_a_copy_of_it()
    {
        var clip = Sample("sample-tv.mp4");

        Assert.True(Mp4Joiner.TryJoin([(clip, 0.0)], Output()));

        Assert.InRange(Mp4Info.Read(Output(), scanStreams: true).VideoDurationSeconds, 7.5, 8.5);
    }
}
