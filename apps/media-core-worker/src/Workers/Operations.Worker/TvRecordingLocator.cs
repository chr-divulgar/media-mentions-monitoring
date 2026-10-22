using System.Globalization;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// One MP4 of a TV source: its path relative to the capture root (or, when Archived, to the archive's folder) and when it
/// started.
/// </summary>
internal sealed record TvRecordingCandidate(string RelativePath, DateTimeOffset Start, bool Archived = false);

/// <summary>The part of a window that falls inside one recording: from..to, both inside that recording's span.</summary>
internal sealed record TvRecordingSlice(TvRecordingCandidate Recording, DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Finds which hourly MP4 holds a given instant. Pure: the caller supplies the files (local folder or
/// FTP listing). A file's name carries the moment its recording STARTED in the source's local time —
/// not the top of the hour — and a restart mid-hour creates another file, so the one that covers an
/// instant is the latest that started before it (and had not planned to end yet).
/// </summary>
internal static class TvRecordingLocator
{
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    // {sourceId}_{yyyy-MM-dd_HH-mm-ss}.mp4 — the id must be exactly that source's, so "canal_1" never
    // claims a file of "canal_10".
    public static bool TryParseStart(string fileName, string sourceId, TimeSpan sourceOffset, out DateTimeOffset start)
    {
        start = default;
        var prefix = sourceId + "_";
        const string Extension = ".mp4";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = fileName[prefix.Length..^Extension.Length];
        if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        start = new DateTimeOffset(local, sourceOffset);
        return true;
    }

    public static TvRecordingCandidate? Find(IReadOnlyList<TvRecordingCandidate> candidates, DateTimeOffset instant) =>
        candidates
            .Where(candidate => candidate.Start <= instant && instant < TvVideoRecording.PlannedEnd(candidate.Start))
            .OrderByDescending(candidate => candidate.Start)
            .FirstOrDefault();

    // Every recording the window touches, in order, each with the part of the window that is in it. A recording's span
    // is from its start to the end it was planned for, cut short by the next one if that started earlier (a restart
    // mid-hour). A hole between two recordings belongs to neither.
    public static IReadOnlyList<TvRecordingSlice> FindOverlapping(
        IReadOnlyList<TvRecordingCandidate> candidates, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var ordered = candidates.OrderBy(candidate => candidate.Start).ToList();
        var slices = new List<TvRecordingSlice>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var end = TvVideoRecording.PlannedEnd(ordered[i].Start);
            if (i + 1 < ordered.Count && ordered[i + 1].Start < end)
            {
                end = ordered[i + 1].Start;
            }

            var from = windowStart > ordered[i].Start ? windowStart : ordered[i].Start;
            var to = windowEnd < end ? windowEnd : end;
            if (from < to)
            {
                slices.Add(new TvRecordingSlice(ordered[i], from, to));
            }
        }

        return slices;
    }

    // The requested window as seconds from the start of the recording, clamped to the span the
    // recording was planned to cover (a window never crosses into the next file).
    public static (double StartSeconds, double EndSeconds) ClipOffsets(
        TvRecordingCandidate recording, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var start = Math.Max(0, (windowStart - recording.Start).TotalSeconds);
        var plannedEnd = (TvVideoRecording.PlannedEnd(recording.Start) - recording.Start).TotalSeconds;
        var end = Math.Min((windowEnd - recording.Start).TotalSeconds, plannedEnd);
        return (start, Math.Max(start, end));
    }
}
