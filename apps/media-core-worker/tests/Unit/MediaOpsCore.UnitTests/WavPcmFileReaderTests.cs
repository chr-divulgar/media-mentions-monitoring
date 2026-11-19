using System.Text;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The WAVs the portal cuts (16 kHz, mono, 16-bit PCM) read as the PCM the speech port takes. Anything else is refused
// by name instead of being sent to an engine as noise.
public sealed class WavPcmFileReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wav-" + Guid.NewGuid().ToString("N"));

    public WavPcmFileReaderTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Write(byte[] bytes, string name = "chunk.wav")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A WAV: RIFF header, optional extra chunks, "fmt ", "data".
    private static byte[] Wav(byte[] pcm, int sampleRate = 16000, short channels = 1, short bits = 16, short format = 1, byte[]? extraChunk = null, int? declaredDataSize = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0); // overall size: not read
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        if (extraChunk is not null)
        {
            writer.Write(Encoding.ASCII.GetBytes("LIST"));
            writer.Write(extraChunk.Length);
            writer.Write(extraChunk);
        }

        if (extraChunk is { Length: > 0 } && extraChunk.Length % 2 == 1)
        {
            writer.Write((byte)0); // chunks are padded to an even size
        }

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write(format);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(declaredDataSize ?? pcm.Length);
        writer.Write(pcm);
        return stream.ToArray();
    }

    private static byte[] Samples(int count) => Enumerable.Range(0, count).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task Reads_the_pcm_of_a_16khz_mono_wav()
    {
        var pcm = Samples(3200);

        var read = await new WavPcmFileReader().ReadAsync(Write(Wav(pcm)));

        Assert.Equal(pcm, read);
    }

    [Fact]
    public async Task Skips_chunks_it_does_not_need_before_the_data()
    {
        var pcm = Samples(640);

        var read = await new WavPcmFileReader().ReadAsync(Write(Wav(pcm, extraChunk: [1, 2, 3, 4, 5])));  // odd size: padded to even

        Assert.Equal(pcm, read);
    }

    [Fact]
    public async Task Takes_what_is_there_when_the_data_size_claims_more_than_the_file_holds()
    {
        var pcm = Samples(640);

        var read = await new WavPcmFileReader().ReadAsync(Write(Wav(pcm, declaredDataSize: int.MaxValue)));

        Assert.Equal(pcm, read);
    }

    [Theory]
    [InlineData(44100, 1, 16, 1)]
    [InlineData(16000, 2, 16, 1)]
    [InlineData(16000, 1, 8, 1)]
    [InlineData(16000, 1, 16, 3)]
    public async Task Refuses_a_format_other_than_16khz_mono_16bit_pcm(int sampleRate, short channels, short bits, short format)
    {
        var path = Write(Wav(Samples(640), sampleRate, channels, bits, format));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new WavPcmFileReader().ReadAsync(path));

        Assert.Contains("16 kHz", exception.Message);
    }

    [Fact]
    public async Task Refuses_a_file_that_is_not_a_wav()
    {
        var path = Write(Encoding.ASCII.GetBytes("ID3 this is an mp3 and not a wav at all"), "a.wav");

        await Assert.ThrowsAsync<InvalidDataException>(() => new WavPcmFileReader().ReadAsync(path));
    }

    [Fact]
    public async Task A_missing_file_is_an_io_error()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => new WavPcmFileReader().ReadAsync(Path.Combine(directory, "nope.wav")));
    }
}
