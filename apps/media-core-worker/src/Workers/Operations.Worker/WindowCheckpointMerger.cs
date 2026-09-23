namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Splices the coverage history of one rotation window back together across a mid-window restart.
///
/// A capture session's counters (captured/silence seconds) are its own — they restart at zero when
/// the worker does, even though the window they describe does not. So the checkpoints a resumed
/// session reports cover only its own part of the hour, starting at the position it picked up
/// from. Taken alone they make everything recorded before the restart look uncovered, which is why
/// an hour we restarted through used to read as one solid block of degraded coverage instead of
/// "recorded, then down, then recording again".
///
/// A snapshot that begins at offset zero owns its window from the start and needs no splicing; one
/// that begins later is partial by construction (see CaptureSession.BuildSnapshot) and gets its
/// counters rebased onto the totals the earlier history had reached by that point.
/// </summary>
public static class WindowCheckpointMerger
{
    public static IReadOnlyList<WindowCheckpoint> Merge(
        IReadOnlyList<WindowCheckpoint>? earlier,
        IReadOnlyList<WindowCheckpoint>? later)
    {
        if (later is not { Count: > 0 })
        {
            return earlier ?? [];
        }

        if (earlier is not { Count: > 0 })
        {
            return later;
        }

        var orderedEarlier = earlier.OrderBy(checkpoint => checkpoint.ElapsedSeconds).ToArray();
        var orderedLater = later.OrderBy(checkpoint => checkpoint.ElapsedSeconds).ToArray();

        // The later snapshot covers the window from its start, so it already tells the whole story.
        if (orderedLater[0].ElapsedSeconds <= 0)
        {
            return orderedLater;
        }

        var resumedAt = orderedLater[0].ElapsedSeconds;
        var anchor = LastAtOrBefore(orderedEarlier, resumedAt);

        return
        [
            .. orderedEarlier.Where(checkpoint => checkpoint.ElapsedSeconds <= resumedAt),
            .. orderedLater.Select(checkpoint => new WindowCheckpoint(
                checkpoint.ElapsedSeconds,
                checkpoint.CapturedSeconds + anchor.CapturedSeconds,
                checkpoint.SilenceFilledSeconds + anchor.SilenceFilledSeconds)),
        ];
    }

    private static WindowCheckpoint LastAtOrBefore(IReadOnlyList<WindowCheckpoint> ordered, double elapsedSeconds)
    {
        WindowCheckpoint? found = null;
        foreach (var checkpoint in ordered)
        {
            if (checkpoint.ElapsedSeconds > elapsedSeconds)
            {
                break;
            }

            found = checkpoint;
        }

        // The earlier history starts after the point the later snapshot resumed from — nothing of
        // it precedes the splice, so there are no totals to carry over.
        return found ?? new WindowCheckpoint(0, 0, 0);
    }
}
