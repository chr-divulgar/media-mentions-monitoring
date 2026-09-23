using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

using Pipeline = InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionPipeline;

public sealed class StripTextPrefixOverlapTests
{
    [Fact]
    public void Should_strip_a_seven_word_overlap_even_with_a_stray_trailing_word_in_the_previous_chunk()
    {
        // Real reported case: "traía" is a stray/mistranscribed word at the very tail of the
        // previous chunk that isn't part of the actual overlap — without tolerating it, the
        // suffix-anchored search never lines up with the 7-word duplicate that follows it.
        var prevText = "quisiera preguntarle al doctor Álvaro forero qué tan complicada es traía";
        var newText = "el doctor Álvaro forero qué tan complicada es esa situación de ecopetrol Y qué vamos a hacer para recuperarla";

        var result = Pipeline.StripTextPrefixOverlap(prevText, newText, minOverlapWords: 4);

        // The whole duplicated span ("doctor Álvaro forero...es") is removed from the NEW chunk's
        // text — it still exists exactly once overall, in the previous (unmodified) chunk's text.
        Assert.DoesNotContain("forero", result);
        Assert.Contains("esa situación de ecopetrol", result);
    }

    [Fact]
    public void Should_strip_a_classic_prefix_overlap_with_no_trailing_noise()
    {
        var prevText = "y con esto llegamos al final de nuestra emisión hoy en esta tarde";
        var newText = "hoy en esta tarde quiero comentarles sobre el nuevo proyecto de la alcaldía";

        var result = Pipeline.StripTextPrefixOverlap(prevText, newText, minOverlapWords: 4);

        Assert.Equal("quiero comentarles sobre el nuevo proyecto de la alcaldía", result);
    }

    [Fact]
    public void Should_not_remove_anything_when_there_is_no_real_overlap()
    {
        var prevText = "buenos días a todos los oyentes de esta emisora";
        var newText = "vamos ahora con las noticias de la mañana en la región";

        var result = Pipeline.StripTextPrefixOverlap(prevText, newText, minOverlapWords: 4);

        Assert.Equal(newText, result);
    }

    [Fact]
    public void Should_not_match_a_short_coincidental_phrase_below_the_floor()
    {
        var prevText = "todos sabemos que la situación de la región";
        // Only "de la" (2 words) coincidentally repeats — below minOverlapWords=4, must not merge.
        var newText = "de la nada empezó a llover en toda la ciudad";

        var result = Pipeline.StripTextPrefixOverlap(prevText, newText, minOverlapWords: 4);

        Assert.Equal(newText, result);
    }
}
