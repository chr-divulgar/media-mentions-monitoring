using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Turns one hour's worth of WebNewsCaptureUseCase incidents into a status plus two half-hour
/// segments, reusing the same captured/gap/no-session vocabulary (and colors) the audio timeline
/// already renders with — a cycle runs every 30 minutes, so a half-hour segment is this row's
/// natural unit, the way a checkpoint-derived slice is for a recording session.
/// </summary>
public static class WebNewsHourStatusBuilder
{
    private const double HalfHourSeconds = 1800;

    public static (string Status, IReadOnlyList<CaptureSegmentEntry>? Segments) Build(
        IReadOnlyList<SourceIncident> hourIncidents, TimeSpan bogotaOffset)
    {
        if (!hourIncidents.Any(incident => incident.Kind is IncidentKinds.CycleOk or IncidentKinds.CycleFailed))
        {
            // Not "down" — nothing ever ran in this hour (before web news was enabled, a future
            // hour, or one before the feature existed). Distinct from a half that ran and failed.
            return ("no-data", null);
        }

        CaptureSegmentEntry[] segments =
        [
            BuildSegment(hourIncidents, firstHalf: true, bogotaOffset, 0, HalfHourSeconds),
            BuildSegment(hourIncidents, firstHalf: false, bogotaOffset, HalfHourSeconds, HalfHourSeconds * 2),
        ];

        var status = segments.All(segment => segment.State == "captured") ? "ok"
            : segments.All(segment => segment.State == "no-session") ? "no-session"
            : "gap-filled";

        return (status, segments);
    }

    private static CaptureSegmentEntry BuildSegment(
        IReadOnlyList<SourceIncident> hourIncidents, bool firstHalf, TimeSpan bogotaOffset, double startSeconds, double endSeconds)
    {
        var halfIncidents = hourIncidents.Where(incident => IsInHalf(incident, firstHalf, bogotaOffset)).ToArray();

        // A cycle that never completed (crashed, or just never ran this half) reads the same as
        // one that explicitly failed — both mean "no evidence this half worked".
        var ranCleanly = halfIncidents.Any(incident => incident.Kind == IncidentKinds.CycleOk);
        var hadArticleProblems = halfIncidents.Any(incident => incident.Kind is IncidentKinds.ArticleFailed or IncidentKinds.SendFailed);

        var state = ranCleanly ? (hadArticleProblems ? "gap" : "captured") : "no-session";
        return new CaptureSegmentEntry { StartSeconds = startSeconds, EndSeconds = endSeconds, State = state };
    }

    private static bool IsInHalf(SourceIncident incident, bool firstHalf, TimeSpan bogotaOffset)
    {
        var minute = incident.At.ToOffset(bogotaOffset).Minute;
        return firstHalf ? minute < 30 : minute >= 30;
    }
}
