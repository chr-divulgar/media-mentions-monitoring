namespace MediaOpsCore.Workers.Operations;

public sealed record CaptureSegment(double StartSeconds, double EndSeconds, string State);

/// <summary>
/// Turns a sparse list of periodic window checkpoints (see WindowCheckpoint) into a timeline of
/// captured/gap/no-session segments covering [0, windowEndSeconds). Pure function, no I/O — kept
/// separate from CaptureStatusSnapshotProvider so it can be unit tested directly.
///
/// Offsets are positions in the recording, not wall-clock offsets (see WindowCheckpoint), and the
/// session brackets every snapshot with both ends of the window, so the last slice is always backed
/// by a real sample. A window whose last checkpoint falls short of windowEndSeconds is one whose
/// recording stopped there — the remainder is reported as no-session rather than assumed.
/// </summary>
public static class CaptureSegmentBuilder
{
    // A slice counts as "captured" when at least this fraction of its duration is backed by real
    // audio, and as "the worker wasn't recording" when at least this fraction is bridge silence.
    private const double DominantShareThreshold = 0.9;

    private const string CapturedState = "captured";
    private const string GapState = "gap";
    private const string NoSessionState = "no-session";

    public static IReadOnlyList<CaptureSegment> Build(IReadOnlyList<WindowCheckpoint> checkpoints, double windowEndSeconds)
    {
        if (windowEndSeconds <= 0)
        {
            return [];
        }

        if (checkpoints.Count == 0)
        {
            return [new CaptureSegment(0, windowEndSeconds, NoSessionState)];
        }

        // Never report on time that hasn't elapsed: a sample taken either side of a rotation can
        // carry an offset belonging to a different window, and pairing it with a real one would
        // otherwise paint a segment across the rest of the hour — into the future.
        var ordered = checkpoints
            .Where(c => c.ElapsedSeconds >= 0 && c.ElapsedSeconds <= windowEndSeconds)
            .OrderBy(c => c.ElapsedSeconds)
            .ToArray();

        if (ordered.Length == 0)
        {
            return [new CaptureSegment(0, windowEndSeconds, NoSessionState)];
        }

        var segments = new List<CaptureSegment>();

        // Before the first checkpoint, the session either didn't exist yet this hour (mid-hour
        // start) or simply hasn't been sampled yet — either way there is no captured-audio signal
        // for that span, so it reads the same as "worker not running".
        if (ordered[0].ElapsedSeconds > 0)
        {
            segments.Add(new CaptureSegment(0, ordered[0].ElapsedSeconds, NoSessionState));
        }

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var start = ordered[i];
            var end = ordered[i + 1];
            if (end.ElapsedSeconds <= start.ElapsedSeconds)
            {
                continue;
            }

            var duration = end.ElapsedSeconds - start.ElapsedSeconds;
            var capturedDelta = end.CapturedSeconds - start.CapturedSeconds;
            var silenceDelta = end.SilenceFilledSeconds - start.SilenceFilledSeconds;
            segments.Add(new CaptureSegment(
                start.ElapsedSeconds, end.ElapsedSeconds, ResolveState(duration, capturedDelta, silenceDelta)));
        }

        // The recording never reached the end of the window, so nothing is known about the rest
        // of it — that is exactly the "worker wasn't capturing this" case, not a state to extend.
        var lastCovered = ordered[^1].ElapsedSeconds;
        if (lastCovered < windowEndSeconds)
        {
            segments.Add(new CaptureSegment(lastCovered, windowEndSeconds, NoSessionState));
        }

        return segments;
    }

    /// <summary>
    /// Bridge silence is not a recording failure — it is what the worker writes to keep a file
    /// covering its whole rotation window across a span it wasn't running for (a restart, or a
    /// source excluded and later recovered). Reporting it as a partial-coverage "gap" is what made
    /// a single mid-hour restart paint the entire hour as degraded; it belongs in the same bucket
    /// as "no session", which is what actually happened. A genuine gap — the recording advanced
    /// but neither real audio nor bridge silence accounts for it — keeps its own state.
    /// </summary>
    private static string ResolveState(double duration, double capturedDelta, double silenceDelta)
    {
        if (capturedDelta / duration >= DominantShareThreshold)
        {
            return CapturedState;
        }

        return silenceDelta / duration >= DominantShareThreshold ? NoSessionState : GapState;
    }
}
