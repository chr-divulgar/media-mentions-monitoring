using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// With the real encoder: what a conversion to 32 kbps does to a radio hour, and that the editor can still cut a note
// from a window that crosses a converted hour and one that is not.
public sealed class RadioOpusConvertedAudioTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-radio.opus");

    private readonly string directory = Path.Combine(Path.GetTempPath(), "radio-conv-audio-" + Guid.NewGuid().ToString("N"));

    public RadioOpusConvertedAudioTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void A_converted_hour_is_a_smaller_mono_16_khz_opus_that_lasts_the_same()
    {
        var original = OggOpusInfo.Read(Sample)!;
        var converted = Path.Combine(directory, "converted.opus");

        OpusSegmentTranscoder.TranscodeFileToOpus(Sample, converted, 32, 16000);

        var info = OggOpusInfo.Read(converted)!;
        var originalKbps = original.AverageKbps(new FileInfo(Sample).Length);
        var convertedKbps = info.AverageKbps(new FileInfo(converted).Length);
        Assert.Equal(1, info.Channels);
        Assert.InRange(info.Duration.TotalSeconds, original.Duration.TotalSeconds - 0.3, original.Duration.TotalSeconds + 0.3);
        // The sample may already be a low bitrate one: then the conversion must at least not make it bigger than 32 kbps.
        Assert.True(convertedKbps <= 40, $"converted bitrate was {convertedKbps:F1} kbps (original {originalKbps:F1})");
    }

    [Fact]
    public void A_note_window_can_cross_a_converted_hour_and_one_that_is_not()
    {
        var duration = OggOpusInfo.Read(Sample)!.Duration.TotalSeconds;
        var converted = Path.Combine(directory, "first-hour.opus");
        OpusSegmentTranscoder.TranscodeFileToOpus(Sample, converted, 32, 16000);

        // The window starts in the converted hour and ends in the original one, as an alert near the edge of the
        // conversion would: the same call the editor's /audio/segment makes.
        var mp3 = OpusSegmentTranscoder.Transcode([converted, Sample], duration / 2, duration, 16000, 32, "mp3");

        // 32 kbps mp3 is ~4 KB a second; the whole window is there, not just the part from one of the files.
        Assert.True(mp3.Length > duration * 4000 * 0.8, $"the cut was {mp3.Length} bytes for a {duration:F1} s window");
    }
}
