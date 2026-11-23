using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// An hourly radio file is Ogg Opus; its duration is read from the page headers (no decoding), so the bitrate of
// 25,000 files can be told without opening an encoder. The fixtures are a real radio opus and what the worker's own
// encoder makes of it.
public sealed class OggOpusInfoTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-radio.opus");

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ogg-" + Guid.NewGuid().ToString("N"));

    public OggOpusInfoTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Copy(string name)
    {
        var path = Path.Combine(directory, name + ".opus");
        File.Copy(Sample, path);
        return path;
    }

    [Fact]
    public void Reads_the_duration_and_channels_of_an_opus_file_without_decoding_it()
    {
        var info = OggOpusInfo.Read(Sample);

        Assert.NotNull(info);
        Assert.Equal(1, info.Channels);
        Assert.True(info.Duration > TimeSpan.FromSeconds(1), $"duration was {info.Duration}");
    }

    [Fact]
    public void Agrees_with_what_the_encoder_wrote_when_a_file_is_converted_to_32_kbps()
    {
        var original = OggOpusInfo.Read(Sample)!;
        var converted = Path.Combine(directory, "converted.opus");

        OpusSegmentTranscoder.TranscodeFileToOpus(Sample, converted, 32, 16000);
        var result = OggOpusInfo.Read(converted);

        Assert.NotNull(result);
        Assert.Equal(1, result.Channels);
        Assert.InRange(result.Duration.TotalSeconds, original.Duration.TotalSeconds - 0.3, original.Duration.TotalSeconds + 0.3);
    }

    [Fact]
    public void Adds_up_the_parts_of_a_file_that_was_chained_after_a_restart()
    {
        // The capture chains a resume file onto the hour it was recording: the Ogg then holds two streams.
        var single = OggOpusInfo.Read(Sample)!;
        var joined = Path.Combine(directory, "joined.opus");
        OpusFileJoiner.Join([Copy("first"), Copy("second")], joined);

        var info = OggOpusInfo.Read(joined);

        Assert.NotNull(info);
        Assert.InRange(info.Duration.TotalSeconds, single.Duration.TotalSeconds * 2 - 0.4, single.Duration.TotalSeconds * 2 + 0.4);
    }

    [Fact]
    public void A_file_cut_short_gives_the_duration_of_what_is_complete()
    {
        var full = OggOpusInfo.Read(Sample)!;
        var path = Copy("cut");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var info = OggOpusInfo.Read(path);

        Assert.NotNull(info);
        Assert.True(info.Duration > TimeSpan.Zero && info.Duration < full.Duration, $"cut duration {info.Duration}, full {full.Duration}");
    }

    [Fact]
    public void A_file_that_is_not_opus_gives_nothing()
    {
        var text = Path.Combine(directory, "notes.opus");
        File.WriteAllText(text, "this is not an ogg file at all, just text");

        Assert.Null(OggOpusInfo.Read(text));
        Assert.Null(OggOpusInfo.Read(Path.Combine(directory, "missing.opus")));
    }

    [Fact]
    public void The_average_bitrate_follows_the_size_and_the_duration()
    {
        var info = new OggOpusInfo(1, 16000, TimeSpan.FromSeconds(3600));

        Assert.Equal(64.0, info.AverageKbps(28_800_000), precision: 1);
        Assert.Equal(32.0, info.AverageKbps(14_400_000), precision: 1);
        Assert.Equal(0, new OggOpusInfo(1, 16000, TimeSpan.Zero).AverageKbps(1000));
    }
}
