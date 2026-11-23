using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Radio is recorded at 32 kbps (what a note's mp3 has: mono, 16 kHz, 32 kbps); everything else keeps the general bitrate.
public sealed class RadioOpusQualityOptionsTests
{
    [Fact]
    public void Radio_is_recorded_at_32_kbps_and_the_other_media_at_the_general_bitrate()
    {
        var options = new OperationsWorkerOptions();

        Assert.Equal(32, options.OpusBitrateKbpsFor("radio"));
        Assert.Equal(32, options.OpusBitrateKbpsFor("Radio"));
        Assert.Equal(64, options.OpusBitrateKbpsFor("television"));
        Assert.Equal(64, options.OpusBitrateKbpsFor("tv"));
        Assert.Equal(64, options.OpusBitrateKbpsFor(null));
    }

    [Fact]
    public void The_general_bitrate_does_not_change_what_radio_gets()
    {
        var options = new OperationsWorkerOptions { DefaultOpusBitrateKbps = 48 };

        Assert.Equal(32, options.OpusBitrateKbpsFor("radio"));
        Assert.Equal(48, options.OpusBitrateKbpsFor("television"));
    }

    [Fact]
    public void The_options_file_can_change_the_radio_bitrate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"worker-options-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "radioOpusBitrateKbps": 24, "defaultOpusBitrateKbps": 56 }""");

            var options = OperationsWorkerOptionsLoader.Load(path);

            Assert.Equal(24, options.OpusBitrateKbpsFor("radio"));
            Assert.Equal(56, options.OpusBitrateKbpsFor("television"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_the_setting_the_options_file_leaves_radio_at_32()
    {
        var options = OperationsWorkerOptionsLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Equal(32, options.OpusBitrateKbpsFor("radio"));
    }
}
