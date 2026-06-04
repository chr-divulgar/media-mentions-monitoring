using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class CanaryRolloutTuner
{
    private readonly OperationsWorkerOptions options;

    public CanaryRolloutTuner(OperationsWorkerOptions options)
    {
        this.options = options;
        options.CanaryPlatformPercent = ClampPercent(options.CanaryPlatformPercent);
    }

    public CanaryTuningDecision Apply(FunctionalParityReport report)
    {
        if (!options.EnableCanaryMode)
        {
            return new CanaryTuningDecision(
                PreviousPercent: options.CanaryPlatformPercent,
                CurrentPercent: options.CanaryPlatformPercent,
                Increased: false,
                Decreased: false,
                Reason: "canary-disabled");
        }

        var previous = options.CanaryPlatformPercent;
        var current = previous;
        var increased = false;
        var decreased = false;

        if (report.OverallParityPercent >= options.ShadowParityMinimumPercent)
        {
            current = Math.Min(options.CanaryPlatformMaxPercent, current + options.CanaryIncreaseStepPercent);
            increased = current > previous;
        }
        else
        {
            current = Math.Max(options.CanaryPlatformMinPercent, current - options.CanaryDecreaseStepPercent);
            decreased = current < previous;
        }

        options.CanaryPlatformPercent = ClampPercent(current);

        return new CanaryTuningDecision(
            PreviousPercent: previous,
            CurrentPercent: options.CanaryPlatformPercent,
            Increased: increased,
            Decreased: decreased,
            Reason: report.OverallParityPercent >= options.ShadowParityMinimumPercent ? "parity-ok" : "parity-below-threshold");
    }

    private int ClampPercent(int percent)
    {
        var min = Math.Min(options.CanaryPlatformMinPercent, options.CanaryPlatformMaxPercent);
        var max = Math.Max(options.CanaryPlatformMinPercent, options.CanaryPlatformMaxPercent);
        return Math.Clamp(percent, min, max);
    }
}