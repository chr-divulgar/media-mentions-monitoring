using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Pure choice of which archived day folders are past their retention. The archive's base folder is shared
/// with other things (a website), so this only ever looks under the two media roots, only at names shaped
/// exactly like {yyyy}/{MM}/{dd}, and only returns whole day folders — never a file, never anything else.
/// </summary>
internal static partial class ArchiveRemoteRetention
{
    // Matches the folders InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName writes locally and
    // ArchiveUpload mirrors in the archive.
    public static readonly string[] MediaRoots = ["radio", "television"];

    // A day folder is expired when it is dated before today minus this many days; and a retention of 1 to 6
    // days is a typo, not a policy, so it counts as 7. 0 (or less) means never delete.
    public static int EffectiveDays(int configured) => configured <= 0 ? 0 : Math.Max(7, configured);

    // True for exactly "radio/2026/07/11" and "television/2026/07/11"-shaped paths: what the worker is
    // allowed to delete, checked once more right before each deletion.
    public static bool IsDayFolder(string relativePath) => DayFolderPattern().IsMatch(relativePath);

    public static async Task<IReadOnlyList<string>> FindExpiredDayFoldersAsync(
        IArchiveStorage storage, DateOnly today, int retentionDays, CancellationToken cancellationToken)
    {
        var days = EffectiveDays(retentionDays);
        if (days == 0)
        {
            return [];
        }

        var cutoff = today.AddDays(-days);
        var expired = new List<(DateOnly Date, string Path)>();

        foreach (var media in MediaRoots)
        {
            foreach (var year in await NumericChildrenAsync(storage, media, 4, cancellationToken).ConfigureAwait(false))
            {
                if (int.Parse(year) > cutoff.Year)
                {
                    continue;
                }

                foreach (var month in await NumericChildrenAsync(storage, $"{media}/{year}", 2, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var day in await NumericChildrenAsync(storage, $"{media}/{year}/{month}", 2, cancellationToken).ConfigureAwait(false))
                    {
                        if (DateOnly.TryParseExact($"{year}-{month}-{day}", "yyyy-MM-dd", out var date) && date < cutoff)
                        {
                            expired.Add((date, $"{media}/{year}/{month}/{day}"));
                        }
                    }
                }
            }
        }

        return expired.OrderBy(entry => entry.Date).ThenBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path).ToList();
    }

    // The sub-folders whose name is exactly that many digits: anything else in there is not ours to look at.
    private static async Task<IReadOnlyList<string>> NumericChildrenAsync(
        IArchiveStorage storage, string directory, int digits, CancellationToken cancellationToken) =>
        (await storage.ListDirectoriesAsync(directory, cancellationToken).ConfigureAwait(false))
            .Where(name => name.Length == digits && name.All(char.IsAsciiDigit))
            .ToList();

    [GeneratedRegex(@"^(radio|television)/\d{4}/\d{2}/\d{2}$")]
    private static partial Regex DayFolderPattern();
}

/// <summary>
/// Deletes from the archive the radio and TV days older than ArchiveRemoteRetentionDays (90 by default) so the
/// server does not fill up. Runs when the worker starts and then every few hours; a few day folders per pass at
/// most, so a wrong clock or setting can never empty the archive in one go. Everything it deletes is logged.
/// </summary>
public sealed class ArchiveRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    // The ceiling per pass. Day folders are deleted oldest first, so the backlog drains over a few passes.
    private const int MaxDayFoldersPerPass = 5;

    private readonly OperationsWorkerOptions options;
    private readonly IArchiveStorage storage;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<ArchiveRetentionWorker> logger;
    private readonly Func<DateTimeOffset> clock;

    public ArchiveRetentionWorker(
        OperationsWorkerOptions options,
        IArchiveStorage storage,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<ArchiveRetentionWorker> logger,
        Func<DateTimeOffset>? clock = null)
    {
        this.options = options;
        this.storage = storage;
        this.incidentRecorder = incidentRecorder;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[ArchiveRetentionWorker] Retention pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // Internal so a test can drive one pass without waiting on the timer.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var days = ArchiveRemoteRetention.EffectiveDays(options.ArchiveRemoteRetentionDays);
        if (days == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(clock().ToOffset(BogotaOffset).DateTime);

        IReadOnlyList<string> expired;
        try
        {
            expired = await ArchiveRemoteRetention.FindExpiredDayFoldersAsync(storage, today, days, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without a trustworthy listing nothing is deleted.
            logger.LogWarning(exception, "[ArchiveRetentionWorker] Could not list the archive; nothing deleted.");
            return;
        }

        foreach (var dayFolder in expired.Take(MaxDayFoldersPerPass))
        {
            if (!ArchiveRemoteRetention.IsDayFolder(dayFolder))
            {
                continue;
            }

            try
            {
                await storage.DeleteDirectoryAsync(dayFolder, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("[ArchiveRetentionWorker] Deleted {Folder} from the archive (older than {Days} days).", dayFolder, days);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[ArchiveRetentionWorker] Could not delete {Folder} from the archive.", dayFolder);
                incidentRecorder.Record(
                    IncidentKinds.WorkerSourceId, IncidentKinds.PurgeFailed, $"No se pudo borrar del servidor de archivo {dayFolder}: {exception.Message}");
            }
        }

        if (expired.Count > MaxDayFoldersPerPass)
        {
            logger.LogInformation("[ArchiveRetentionWorker] {Remaining} more expired day folder(s) wait for the next pass.", expired.Count - MaxDayFoldersPerPass);
        }
    }
}
