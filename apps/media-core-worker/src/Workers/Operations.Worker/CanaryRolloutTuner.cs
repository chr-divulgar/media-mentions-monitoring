using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class CanaryRolloutTuner
{
    private readonly OperationsWorkerOptions options;
    private int stableParityCycles;

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
        var milestones = ResolveMilestones();

        if (report.OverallParityPercent >= options.ShadowParityMinimumPercent)
        {
            stableParityCycles++;
            var nextMilestone = FindNextMilestone(previous, milestones);

            if (nextMilestone is not null && stableParityCycles >= Math.Max(1, options.CanaryStableCyclesForPromotion))
            {
                current = nextMilestone.Value;
                stableParityCycles = 0;
            }
            else
            {
                current = Math.Min(options.CanaryPlatformMaxPercent, current + options.CanaryIncreaseStepPercent);
            }

            increased = current > previous;
        }
        else
        {
            stableParityCycles = 0;
            var rollbackMilestone = FindPreviousMilestone(previous, milestones);

            if (rollbackMilestone is not null)
            {
                current = rollbackMilestone.Value;
            }
            else
            {
                current = Math.Max(options.CanaryPlatformMinPercent, current - options.CanaryDecreaseStepPercent);
            }

            decreased = current < previous;
        }

        options.CanaryPlatformPercent = ClampPercent(current);

        return new CanaryTuningDecision(
            PreviousPercent: previous,
            CurrentPercent: options.CanaryPlatformPercent,
            Increased: increased,
            Decreased: decreased,
            Reason: BuildReason(report, previous, options.CanaryPlatformPercent));
    }

    private string BuildReason(FunctionalParityReport report, int previous, int current)
    {
        if (report.OverallParityPercent < options.ShadowParityMinimumPercent)
        {
            return current < previous ? "parity-below-threshold-rollback" : "parity-below-threshold";
        }

        return current > previous ? "parity-ok-promotion" : "parity-ok";
    }

    private int[] ResolveMilestones()
    {
        var values = options.CanaryPromotionMilestones
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, out var value) ? value : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => ClampPercent(value!.Value))
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        if (values.Length == 0)
        {
            return new[] { ClampPercent(options.CanaryPlatformMinPercent), ClampPercent(options.CanaryPlatformMaxPercent) }
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
        }

        return values;
    }

    private static int? FindNextMilestone(int current, IReadOnlyList<int> milestones)
    {
        foreach (var milestone in milestones)
        {
            if (milestone > current)
            {
                return milestone;
            }
        }

        return null;
    }

    private static int? FindPreviousMilestone(int current, IReadOnlyList<int> milestones)
    {
        for (var index = milestones.Count - 1; index >= 0; index--)
        {
            if (milestones[index] < current)
            {
                return milestones[index];
            }
        }

        return null;
    }

    private int ClampPercent(int percent)
    {
        var min = Math.Min(options.CanaryPlatformMinPercent, options.CanaryPlatformMaxPercent);
        var max = Math.Max(options.CanaryPlatformMinPercent, options.CanaryPlatformMaxPercent);
        return Math.Clamp(percent, min, max);
    }
}