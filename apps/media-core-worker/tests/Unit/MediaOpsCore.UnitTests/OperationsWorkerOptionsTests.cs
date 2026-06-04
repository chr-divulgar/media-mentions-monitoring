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
}