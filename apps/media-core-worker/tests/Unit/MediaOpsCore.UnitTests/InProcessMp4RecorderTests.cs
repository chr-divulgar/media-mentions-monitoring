using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The TV recording keeps the video as YouTube serves it (copied, no CPU) but re-encodes the audio to a small mono
// stream: stereo 128 kbps audio was a third of every file, and speech recognition does not need it.
public sealed class InProcessMp4RecorderTests
{
    // 6 s, 426x240 H.264 with a keyframe every 2 s, AAC stereo 44.1 kHz at 128 kbps.
    private static string StereoSource => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-tv-stereo.mp4");

    private static string NewOutput() => Path.Combine(Path.GetTempPath(), $"recorder-{Guid.NewGuid():N}.mp4");

    [Fact]
    public void Record_reencodes_the_audio_to_mono_16khz_and_copies_the_video_untouched()
    {
        var output = NewOutput();
        try
        {
            // The source is a finite file, so the "stream" ends before the hour does and Record says so.
            var stopReason = InProcessMp4Recorder.Record(
                [StereoSource], TimeSpan.FromHours(1), output, CancellationToken.None, new TvAudioSettings(32, 16000));
            Assert.NotNull(stopReason);

            var original = Mp4Info.Read(StereoSource);
            var info = Mp4Info.Read(output);
            Assert.True(info.HasVideo && info.HasAudio);
            Assert.Equal(1, info.AudioChannels);
            Assert.Equal(16000, info.AudioSampleRate);
            Assert.Equal("aac", info.AudioCodec);
            // The video stream is the source's own: same size, no re-encode.
            Assert.Equal((original.VideoWidth, original.VideoHeight), (info.VideoWidth, info.VideoHeight));
            Assert.InRange(info.DurationSeconds, original.DurationSeconds - 0.3, original.DurationSeconds + 0.3);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Record_makes_the_audio_far_smaller_than_the_stereo_original()
    {
        var copied = NewOutput();
        var reencoded = NewOutput();
        try
        {
            InProcessMp4Recorder.Record([StereoSource], TimeSpan.FromHours(1), copied, CancellationToken.None);
            InProcessMp4Recorder.Record([StereoSource], TimeSpan.FromHours(1), reencoded, CancellationToken.None, new TvAudioSettings(32, 16000));

            // Same picture in both; the saving is all audio: 128 kbps stereo against 32 kbps mono.
            Assert.True(new FileInfo(reencoded).Length < new FileInfo(copied).Length - 40_000);
        }
        finally
        {
            File.Delete(copied);
            File.Delete(reencoded);
        }
    }

    [Fact]
    public void Record_without_audio_settings_copies_the_audio_as_it_always_did()
    {
        var output = NewOutput();
        try
        {
            InProcessMp4Recorder.Record([StereoSource], TimeSpan.FromHours(1), output, CancellationToken.None);

            var info = Mp4Info.Read(output);
            Assert.Equal(2, info.AudioChannels);
            Assert.Equal(44100, info.AudioSampleRate);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void A_recording_with_reencoded_audio_can_still_be_cut_into_a_clip_with_both_streams()
    {
        var output = NewOutput();
        var clipPath = NewOutput();
        try
        {
            InProcessMp4Recorder.Record([StereoSource], TimeSpan.FromHours(1), output, CancellationToken.None, new TvAudioSettings(32, 16000));

            var clip = Mp4SegmentCutter.Cut(output, 2.5, 5.0, clipPath, CancellationToken.None);

            Assert.NotNull(clip);
            var info = Mp4Info.Read(clipPath);
            Assert.True(info.HasVideo && info.HasAudio);
            // From the keyframe at 2 s to 5 s: audio and video end together, so the picture and sound stayed in step.
            Assert.InRange(info.DurationSeconds, 2.5, 3.5);
        }
        finally
        {
            File.Delete(output);
            File.Delete(clipPath);
        }
    }

    [Fact]
    public void Record_keeps_the_audio_in_step_with_the_video_from_the_start()
    {
        var output = NewOutput();
        try
        {
            InProcessMp4Recorder.Record([StereoSource], TimeSpan.FromHours(1), output, CancellationToken.None, new TvAudioSettings(32, 16000));

            var info = Mp4Info.Read(output, scanStreams: true);
            // Both streams begin together (within an audio frame) and last as long as each other.
            Assert.InRange(Math.Abs(info.AudioStartSeconds - info.VideoStartSeconds), 0, 0.15);
            Assert.InRange(Math.Abs(info.AudioDurationSeconds - info.VideoDurationSeconds), 0, 0.25);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
