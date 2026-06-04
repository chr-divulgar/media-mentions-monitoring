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

    [Fact]
    public void Apply_should_promote_to_50_after_configured_stable_cycles()
    {
        var options = new OperationsWorkerOptions
        {
            EnableCanaryMode = true,
            CanaryPlatformMinPercent = 10,
            CanaryPlatformMaxPercent = 100,
            CanaryPlatformPercent = 20,
            CanaryIncreaseStepPercent = 0,
            CanaryDecreaseStepPercent = 5,
            CanaryPromotionMilestones = "20,50,100",
            CanaryStableCyclesForPromotion = 2,
            ShadowParityMinimumPercent = 95
        };

        var tuner = new CanaryRolloutTuner(options);
        var report = new FunctionalParityReport(DateTimeOffset.UtcNow, Array.Empty<CollectionParityResult>(), 98, true);

        var firstDecision = tuner.Apply(report);
        var secondDecision = tuner.Apply(report);

        Assert.Equal(20, firstDecision.CurrentPercent);
        Assert.Equal(50, secondDecision.CurrentPercent);
        Assert.True(secondDecision.Increased);
    }

    [Fact]
    public void Apply_should_promote_to_100_and_rollback_to_50_when_threshold_breaks()
    {
        var options = new OperationsWorkerOptions
        {
            EnableCanaryMode = true,
            CanaryPlatformMinPercent = 10,
            CanaryPlatformMaxPercent = 100,
            CanaryPlatformPercent = 50,
            CanaryIncreaseStepPercent = 0,
            CanaryDecreaseStepPercent = 5,
            CanaryPromotionMilestones = "20,50,100",
            CanaryStableCyclesForPromotion = 1,
            ShadowParityMinimumPercent = 95
        };

        var tuner = new CanaryRolloutTuner(options);
        var parityOk = new FunctionalParityReport(DateTimeOffset.UtcNow, Array.Empty<CollectionParityResult>(), 97, true);
        var parityFail = new FunctionalParityReport(DateTimeOffset.UtcNow, Array.Empty<CollectionParityResult>(), 90, false);

        var promotionDecision = tuner.Apply(parityOk);
        var rollbackDecision = tuner.Apply(parityFail);

        Assert.Equal(100, promotionDecision.CurrentPercent);
        Assert.Equal(50, rollbackDecision.CurrentPercent);
        Assert.True(rollbackDecision.Decreased);
        Assert.Equal("parity-below-threshold-rollback", rollbackDecision.Reason);
    }
}