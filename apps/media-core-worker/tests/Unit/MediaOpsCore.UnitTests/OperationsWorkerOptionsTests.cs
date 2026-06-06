using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class OperationsWorkerOptionsTests
{
    [Fact]
    public void Default_heartbeat_interval_should_be_30_seconds()
    {
        var options = new MediaOpsCore.Workers.Operations.OperationsWorkerOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
    }

    [Fact]
    public void Default_continuous_media_allow_list_should_target_radio_and_video()
    {
        var options = new MediaOpsCore.Workers.Operations.OperationsWorkerOptions();

        Assert.Equal("radio,video", options.ContinuousMediaAllowList);
    }

    [Fact]
    public void Default_audio_windows_should_be_30_seconds()
    {
        var options = new MediaOpsCore.Workers.Operations.OperationsWorkerOptions();

        Assert.Equal(30, options.DefaultWavWindowDurationSeconds);
        Assert.Equal(30, options.DefaultOpusFlushIntervalSeconds);
        Assert.Equal(1, options.DefaultOpusRotationIntervalHours);
        Assert.Equal(64, options.DefaultOpusBitrateKbps);
    }
}
