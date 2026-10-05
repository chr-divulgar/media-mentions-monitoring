namespace MediaOpsCore.Workers.Operations;

internal sealed record PendingArchiveFile(string LocalPath, string RelativePath, long Size, DateTime LastWriteUtc);

/// <summary>
/// Pure selection of what still needs archiving: closed files in capture day folders that the
/// ledger does not already have. "Closed" is judged by inactivity — an active hour is written every
/// few seconds (audio flush, continuous video), so a file untouched for a while is finished.
/// </summary>
internal static class ArchiveUpload
{
    public static IReadOnlyList<PendingArchiveFile> FindPendingFiles(
        string audioOutputRootPath,
        Func<string, long, bool> isArchived,
        DateTime nowUtc,
        TimeSpan minIdle)
    {
        var pending = new List<PendingArchiveFile>();

        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(audioOutputRootPath))
        {
            foreach (var file in SafeFiles(dayDir))
            {
                // A .resume is the in-progress half of a resumed hour; it is merged into the real
                // file later and must never be archived on its own.
                if (file.Name.EndsWith(".resume", StringComparison.OrdinalIgnoreCase)
                    || nowUtc - file.LastWriteTimeUtc < minIdle)
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
