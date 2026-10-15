namespace MediaOpsCore.Workers.Operations;

// RelativePath is the LOCAL file (what the ledger and the purge key on); RemoteRelativePath is where it goes
// in the archive. Radio keeps its name there but is Reencode: the hourly opus is rewritten smaller first.
internal sealed record PendingArchiveFile(
    string LocalPath, string RelativePath, long Size, DateTime LastWriteUtc, string RemoteRelativePath, bool Reencode);

/// <summary>
/// Pure selection of what still needs archiving that the ledger does not already have: TV video (MP4) and
/// radio audio (the hourly opus, archived re-encoded to a small mono opus). Everything else — transcriptions, resume files, a TV
/// channel's own audio — is not archived. "Closed" is judged by inactivity — an active hour is written every
/// few seconds (audio flush, continuous video), so a file untouched for a while is finished — and, for the
/// hourly files, by the hour named in the file being over.
/// </summary>
internal static class ArchiveUpload
{
    public static IReadOnlyList<PendingArchiveFile> FindPendingFiles(
        string audioOutputRootPath,
        Func<string, long, bool> isArchived,
        DateTime nowUtc,
        TimeSpan minIdle,
        IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId = null,
        Func<string, string?>? countryOfSource = null)
    {
        var pending = new List<PendingArchiveFile>();

        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(audioOutputRootPath))
        {
            foreach (var file in SafeFiles(dayDir))
            {
                var relative = ToRelativePath(audioOutputRootPath, file.FullName);
                if (!RequiresArchive(relative)
                    || nowUtc - file.LastWriteTimeUtc < minIdle
                    || IsStillRecording(file, nowUtc, offsetBySourceId))
                {
                    continue;
                }

                if (!isArchived(relative, file.Length))
                {
                    pending.Add(new PendingArchiveFile(
                        file.FullName, relative, file.Length, file.LastWriteTimeUtc,
                        ArchiveLayout.BackupPathFor(countryOfSource?.Invoke(file.Directory?.Name ?? string.Empty), relative),
                        Reencode: IsRadioOpus(relative)));
                }
            }
        }

        pending.Sort((a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc));
        return pending;
    }

    // What the archive holds: every MP4 (TV video) and the radio's hourly opus. The purge asks the same
    // question, so the two always agree on which local files must wait for the ledger before they are deleted.
    public static bool RequiresArchive(string relativePath) =>
        relativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || IsRadioOpus(relativePath);

    private static bool IsRadioOpus(string relativePath) =>
        relativePath.StartsWith("radio/", StringComparison.Ordinal)
        && relativePath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);

    // The mtime of a file that is still being written can lag by many minutes (observed on NTFS), so
    // "idle" alone mistakes the current hour's file for a finished one and uploads a growing file.
    // The name carries the start time: an MP4 also the hour it is planned to end at, an hourly opus the
    // end of its hour. Before that it is open — also when a dropped station left its file untouched for a
    // while but may still come back and append to it.
    // The name's clock is the source's own (UtcOffsetMinutes of its country, see TvVideoRecorderWorker);
    // the source id is the file's folder. A source no longer in the catalog falls back to Bogota.
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    private static bool IsStillRecording(FileInfo file, DateTime nowUtc, IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId)
    {
        var match = RecordingStamp.Match(file.Name);
        var isOpus = file.Name.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
        {
            return false;
        }

        var offset = offsetBySourceId is not null
            && file.Directory?.Name is { } sourceId
            && offsetBySourceId.TryGetValue(sourceId, out var sourceOffset) ? sourceOffset : BogotaOffset;
        var begun = new DateTimeOffset(start, offset);
        var plannedEnd = isOpus ? begun.AddHours(1) : TvVideoRecording.PlannedEnd(begun);
        return nowUtc < plannedEnd.UtcDateTime;
    }

    private static readonly System.Text.RegularExpressions.Regex RecordingStamp =
        new(@"_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.(?:mp4|opus)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

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
