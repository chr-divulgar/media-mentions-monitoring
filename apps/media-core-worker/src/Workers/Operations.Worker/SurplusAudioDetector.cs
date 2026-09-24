namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Trips when a source keeps delivering more audio than time passes. A connect burst or clock
/// jitter produces a little surplus now and then; a steady surplus means the server is replaying
/// the same content (a looping file or buffer), which is not a live broadcast worth recording.
/// </summary>
public sealed class SurplusAudioDetector(TimeSpan window, TimeSpan maxSurplusPerWindow)
{
    private DateTimeOffset? windowStart;
    private TimeSpan surplusInWindow;

    public bool RecordSurplus(DateTimeOffset now, TimeSpan surplus)
    {
        if (windowStart is null || now - windowStart.Value >= window)
        {
            windowStart = now;
            surplusInWindow = TimeSpan.Zero;
        }

        surplusInWindow += surplus;
        return surplusInWindow > maxSurplusPerWindow;
    }
}

public sealed class RepeatingStreamException(string message) : InvalidOperationException(message);
