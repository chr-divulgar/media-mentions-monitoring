using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class SpeechEnginesTests
{
    [Fact]
    public void An_unknown_engine_fails_at_startup_and_lists_the_ones_there_are()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SpeechEngines.Create("whisper-local"));

        Assert.Contains("whisper-local", exception.Message);
        Assert.Contains(SpeechEngines.GoogleWeb, exception.Message);
    }

    [Fact]
    public void The_default_engine_is_the_google_web_one()
    {
        // Without the key in the environment it asks for it (so the default engine is the one that was built).
        var previous = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_SPEECH_API_KEY", null);

            var exception = Assert.Throws<InvalidOperationException>(() => SpeechEngines.Create(null));

            Assert.Contains("GOOGLE_SPEECH_API_KEY", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_SPEECH_API_KEY", previous);
        }
    }
}
