namespace MediaOpsCore.Workers.Operations;

public enum DiskSpaceTransition
{
    None,
    BecameLow,
    StillLow,
    Recovered,
}

/// <summary>
/// Pure hysteresis: a `disk-low` incident fires once on the way down, not on every tick while it
/// stays low, and `disk-recovered` only fires once there is real headroom again — without the
/// margin, free space hovering right at the threshold would flap an incident every tick.
/// </summary>
internal static class DiskSpaceAlert
{
    private const double RecoveryMarginGb = 2.0;

    public static DiskSpaceTransition Evaluate(bool wasLow, double freeGb, int minFreeDiskGb)
    {
        var isLow = freeGb < minFreeDiskGb;

        if (isLow)
        {
            return wasLow ? DiskSpaceTransition.StillLow : DiskSpaceTransition.BecameLow;
        }

        if (wasLow && freeGb < minFreeDiskGb + RecoveryMarginGb)
        {
            return DiskSpaceTransition.StillLow;
        }

        return wasLow ? DiskSpaceTransition.Recovered : DiskSpaceTransition.None;
    }
}
