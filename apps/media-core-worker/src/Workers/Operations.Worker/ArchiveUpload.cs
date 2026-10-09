namespace MediaOpsCore.Workers.Operations;

internal sealed record PendingArchiveFile(string LocalPath, string RelativePath, long Size, DateTime LastWriteUtc);

/// <summary>
/// Pure selection of what still needs archiving: closed MP4 files in capture day folders that the
/// ledger does not already have. "Closed" is judged by inactivity — an active hour is written every
/// few seconds (audio flush, continuous video), so a file untouched for a while is finished.
/// </summary>
internal static class ArchiveUpload
{
    public static IReadOnlyList<PendingArchiveFile> FindPendingFiles(
        string audioOutputRootPath,
        Func<string, long, bool> isArchived,
        DateTime nowUtc,
        TimeSpan minIdle,
        IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId = null)
    {
        var pending = new List<PendingArchiveFile>();

        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(audioOutputRootPath))
        {
            foreach (var file in SafeFiles(dayDir))
            {
                // Only TV video is archived for now; audio and its transcription are not (they are
                // kept for the current day and deleted afterwards, see RetentionPurgeWorker).
                if (!file.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || nowUtc - file.LastWriteTimeUtc < minIdle
                    || IsStillRecording(file, nowUtc, offsetBySourceId))
                {
                    continue;
                }

                var relative = ToRelativePath(audioOutputRootPath, file.FullName);
                if (!isArchived(relative, file.Length))
                {
                    pending.Add(new PendingArchiveFile(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        pending.Sort((a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc));
        return pending;
    }

    // The mtime of an MP4 that is still being written can lag by many minutes (observed on NTFS), so
    // "idle" alone mistakes the current hour's file for a finished one and uploads a growing file.
    // The name carries the start time and the hour it is planned to end at; before that it is open.
    // The name's clock is the source's own (UtcOffsetMinutes of its country, see TvVideoRecorderWorker);
    // the source id is the file's folder. A source no longer in the catalog falls back to Bogota.
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    private static bool IsStillRecording(FileInfo file, DateTime nowUtc, IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId)
    {
        var match = RecordingStamp.Match(file.Name);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
        {
            return false;
        }

        var offset = offsetBySourceId is not null
            && file.Directory?.Name is { } sourceId
            && offsetBySourceId.TryGetValue(sourceId, out var sourceOffset) ? sourceOffset : BogotaOffset;
        return nowUtc < TvVideoRecording.PlannedEnd(new DateTimeOffset(start, offset)).UtcDateTime;
    }

    private static readonly System.Text.RegularExpressions.Regex RecordingStamp =
        new(@"_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.mp4$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // The archive mirrors the local layout, always with '/' whatever the OS.
    public static string ToRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static IEnumerable<FileInfo> SafeFiles(string dayDir)
    {
        try
        {
            return new DirectoryInfo(dayDir).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
