using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The body of POST /transcribe, as the portal sends it.
public sealed class TranscribeRequestTests
{
    [Fact]
    public void A_list_of_paths_is_valid_and_the_language_defaults_to_colombian_spanish()
    {
        var request = new TranscribeRequest { AudioPaths = [@"C:\audio\chunk_0.wav", @"C:\audio\chunk_60.wav"] };

        Assert.Null(request.Validate());
        Assert.Equal("es-CO", request.EffectiveLanguage);
    }

    [Fact]
    public void Keeps_the_language_the_caller_asks_for()
    {
        Assert.Equal("es-ES", new TranscribeRequest { AudioPaths = ["a.wav"], Language = " es-ES " }.EffectiveLanguage);
    }

    [Fact]
    public void Refuses_no_paths_or_a_blank_one()
    {
        List<string>?[] cases = [null, [], ["a.wav", ""], ["a.wav", "   "]];

        foreach (var paths in cases)
        {
            Assert.NotNull(new TranscribeRequest { AudioPaths = paths }.Validate());
        }
    }

    [Fact]
    public void Refuses_an_absurd_number_of_files()
    {
        var request = new TranscribeRequest { AudioPaths = Enumerable.Range(0, TranscribeRequest.MaxFiles + 1).Select(i => $"{i}.wav").ToList() };

        Assert.Contains(TranscribeRequest.MaxFiles.ToString(), request.Validate());
    }
}
