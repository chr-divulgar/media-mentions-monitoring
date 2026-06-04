using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class CanaryRolloutTunerTests
{
    [Fact]
    public void Apply_should_increase_percent_when_parity_meets_threshold()
    {
        var options = new OperationsWorkerOptions
        {
            EnableCanaryMode = true,
            CanaryPlatformMinPercent = 10,
            CanaryPlatformMaxPercent = 20,
            CanaryPlatformPercent = 15,
            CanaryIncreaseStepPercent = 5,
            CanaryDecreaseStepPercent = 5,
            ShadowParityMinimumPercent = 95
        };

        var tuner = new CanaryRolloutTuner(options);
        var report = new FunctionalParityReport(DateTimeOffset.UtcNow, Array.Empty<CollectionParityResult>(), 97, true);

        var decision = tuner.Apply(report);

        Assert.Equal(15, decision.PreviousPercent);
        Assert.Equal(20, decision.CurrentPercent);
        Assert.True(decision.Increased);
        Assert.False(decision.Decreased);
    }

    [Fact]
    public void Apply_should_decrease_percent_when_parity_is_below_threshold()
    {
        var options = new OperationsWorkerOptions
        {
            EnableCanaryMode = true,
            CanaryPlatformMinPercent = 10,
            CanaryPlatformMaxPercent = 20,
            CanaryPlatformPercent = 20,
            CanaryIncreaseStepPercent = 5,
            CanaryDecreaseStepPercent = 5,
            ShadowParityMinimumPercent = 95
        };

        var tuner = new CanaryRolloutTuner(options);
        var report = new FunctionalParityReport(DateTimeOffset.UtcNow, Array.Empty<CollectionParityResult>(), 91, false);

        var decision = tuner.Apply(report);

        Assert.Equal(20, decision.PreviousPercent);
        Assert.Equal(15, decision.CurrentPercent);
        Assert.False(decision.Increased);
        Assert.True(decision.Decreased);
    }
}