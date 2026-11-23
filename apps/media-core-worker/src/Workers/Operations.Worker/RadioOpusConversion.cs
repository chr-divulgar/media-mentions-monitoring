namespace MediaOpsCore.Workers.Operations;

internal sealed record RadioOpusCandidate(string Path, string RelativePath, long Size, DateTime LastWriteUtc);

/// <summary>
/// Pure selection for the one-time conversion of the radio hours on disk to 32 kbps: closed hourly opus of radio
/// (never television, never the hour being recorded, never a file written a moment ago) that are still big enough to be
/// at the old bitrate (~28 MB an hour against ~14 MB) and that the converted ledger does not have at that size. The
/// real bitrate is confirmed per file when it is converted, so selecting 25,000 files does not mean opening them.
/// Newest first: that is what is used most, and it frees the most space for the longest.
/// </summary>
internal static class RadioOpusConversion
{
    // Between the ~14 MB of an hour at 32 kbps and the ~28 MB of one at 64: anything under it is already small.
    public const long DefaultMinBytes = 21_000_000;

    public static IReadOnlyList<RadioOpusCandidate> FindCandidates(
        string audioOutputRootPath,
        Func<string, long, bool> isConverted,
        DateTime nowUtc,
        TimeSpan minIdle,
        IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId,
        long minBytes)
    {
        var found = new List<RadioOpusCandidate>();
        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(audioOutputRootPath))
        {
            foreach (var file in ClosedRadioOpus(audioOutputRootPath, dayDir, nowUtc, minIdle, offsetBySourceId))
            {
                var relative = ArchiveUpload.ToRelativePath(audioOutputRootPath, file.FullName);
                if (file.Length >= minBytes && !isConverted(relative, file.Length))
                {
                    found.Add(new RadioOpusCandidate(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        found.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
        return found;
    }

    /// <summary>
    /// The average size, in MB, of the most recent closed radio hours: what the capture is writing now. It is the way to
    /// confirm the new quality before converting what is already there. Null when there is none.
    /// </summary>
    public static double? RecentClosedHoursAverageMb(
        string audioOutputRootPath,
        DateTime nowUtc,
        TimeSpan minIdle,
        IReadOnlyDictionary<string, TimeSpan>? offsetBySourceId,
        int count)
    {
        var recent = RetentionPurge.EnumerateDayFolders(audioOutputRootPath)
            .Where(day => IsRadio(audioOutputRootPath, day.Path))
            .OrderByDescending(day => day.Date)
            .Take(2)
            .SelectMany(day => ClosedRadioOpus(audioOutputRootPath, day.Path, nowUtc, minIdle, offsetBySourceId))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(count)
            .ToArray();

        return recent.Length == 0 ? null : recent.Average(file => file.Length) / 1_000_000.0;
    }

    private static IEnumerable<FileInfo> ClosedRadioOpus(
        string root, string dayDir, DateTime nowUtc, TimeSpan minIdle, IReadOnlyDictionary<string, TimeSpan>? offsets)
    {
        if (!IsRadio(root, dayDir))
        {
            yield break;
        }

        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dayDir).EnumerateFiles("*.opus", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (file.Name.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)
                && nowUtc - file.LastWriteTimeUtc >= minIdle
                && !ArchiveUpload.IsStillRecording(file, nowUtc, offsets))
            {
                yield return file;
            }
        }
    }

    private static bool IsRadio(string root, string dayDir) =>
        ArchiveUpload.ToRelativePath(root, dayDir).StartsWith("radio/", StringComparison.Ordinal);
}
