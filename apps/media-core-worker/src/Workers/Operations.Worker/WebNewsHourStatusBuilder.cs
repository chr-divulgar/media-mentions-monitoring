using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Turns one hour's worth of WebNewsCaptureUseCase incidents into a status plus four quarter-hour
/// segments, reusing the same captured/gap/no-session vocabulary (and colors) the audio timeline
/// already renders with — a cycle runs every 15 minutes (see DiscreteSchedule), so a quarter of an hour is this
/// row's natural unit, the way a checkpoint-derived slice is for a recording session.
/// </summary>
public static class WebNewsHourStatusBuilder
{
    private const double QuarterSeconds = 900;
    private const int QuartersPerHour = 4;

    public static (string Status, IReadOnlyList<CaptureSegmentEntry>? Segments) Build(
        IReadOnlyList<SourceIncident> hourIncidents, TimeSpan bogotaOffset)
    {
        if (!hourIncidents.Any(incident => incident.Kind is IncidentKinds.CycleOk or IncidentKinds.CycleFailed))
        {
            // Not "down" — nothing ever ran in this hour (before web news was enabled, a future
            // hour, or one before the feature existed). Distinct from a half that ran and failed.
            return ("no-data", null);
        }

        var segments = Enumerable.Range(0, QuartersPerHour)
            .Select(quarter => BuildSegment(hourIncidents, quarter, bogotaOffset))
            .ToArray();

        var status = segments.All(segment => segment.State == "captured") ? "ok"
            : segments.All(segment => segment.State == "no-session") ? "no-session"
            : "gap-filled";

        return (status, segments);
    }

    private static CaptureSegmentEntry BuildSegment(IReadOnlyList<SourceIncident> hourIncidents, int quarter, TimeSpan bogotaOffset)
    {
        var quarterIncidents = hourIncidents.Where(incident => QuarterOf(incident, bogotaOffset) == quarter).ToArray();

        // A cycle that never completed (crashed, or just never ran this quarter) reads the same as
        // one that explicitly failed — both mean "no evidence this quarter worked".
        var ranCleanly = quarterIncidents.Any(incident => incident.Kind == IncidentKinds.CycleOk);
        var hadArticleProblems = quarterIncidents.Any(incident => incident.Kind is IncidentKinds.ArticleFailed or IncidentKinds.SendFailed);

        var state = ranCleanly ? (hadArticleProblems ? "gap" : "captured") : "no-session";
        return new CaptureSegmentEntry { StartSeconds = quarter * QuarterSeconds, EndSeconds = (quarter + 1) * QuarterSeconds, State = state };
    }

    private static int QuarterOf(SourceIncident incident, TimeSpan bogotaOffset) =>
        incident.At.ToOffset(bogotaOffset).Minute / (int)(QuarterSeconds / 60);
}
