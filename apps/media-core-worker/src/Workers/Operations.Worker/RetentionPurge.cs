using System.Text.RegularExpressions;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Finds capture-output day folders ({media}/{yyyy}/{MM}/{dd}/{sourceId}/...) older than a cutoff.
/// Pure filesystem scanning, no deletion — RetentionPurgeWorker decides what to do with the result.
///
/// AudioOutputRootPath is often a whole drive (e.g. "D:\"), shared with unrelated folders (Games,
/// Program Files, the recycle bin). Rather than trust a hardcoded media-name allowlist, this only
/// descends into the exact {yyyy}/{MM}/{dd} shape InProcessFfmpegAudioCapturePlugin itself writes
/// (see BuildMediaDirectoryName) — anything else on the drive is structurally invisible to it.
/// </summary>
internal static partial class RetentionPurge
{
    public static IReadOnlyList<string> FindDayFoldersOlderThan(string audioOutputRootPath, DateOnly cutoffExclusive)
    {
        var result = new List<string>();
        if (!Directory.Exists(audioOutputRootPath))
        {
            return result;
        }

        foreach (var mediaDir in SafeSubdirectories(audioOutputRootPath))
        {
            foreach (var yearDir in SafeSubdirectories(mediaDir).Where(d => FourDigits().IsMatch(Name(d))))
            {
                foreach (var monthDir in SafeSubdirectories(yearDir).Where(d => TwoDigits().IsMatch(Name(d))))
                {
                    foreach (var dayDir in SafeSubdirectories(monthDir).Where(d => TwoDigits().IsMatch(Name(d))))
                    {
                        if (TryParseDate(yearDir, monthDir, dayDir, out var date) && date < cutoffExclusive)
                        {
                            result.Add(dayDir);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static bool TryParseDate(string yearDir, string monthDir, string dayDir, out DateOnly date)
    {
        date = default;
        return int.TryParse(Name(yearDir), out var year)
            && int.TryParse(Name(monthDir), out var month)
            && int.TryParse(Name(dayDir), out var day)
            && DateOnly.TryParse($"{year:D4}-{month:D2}-{day:D2}", out date);
    }

    private static string Name(string path) => Path.GetFileName(path);

    // A drive root enumerates things this worker has no business touching (the recycle bin, System
    // Volume Information, ...) — those throw UnauthorizedAccessException on enumeration; skip them
    // rather than let one bad directory abort the whole scan.
    private static IEnumerable<string> SafeSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex FourDigits();

    [GeneratedRegex(@"^\d{2}$")]
    private static partial Regex TwoDigits();
}
