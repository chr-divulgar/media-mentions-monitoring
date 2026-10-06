using System.Globalization;

namespace MediaOpsCore.Workers.Operations;

/// <summary>One MP4 of a TV source: its path relative to the capture root and when it started.</summary>
internal sealed record TvRecordingCandidate(string RelativePath, DateTimeOffset Start);

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
