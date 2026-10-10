using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Keeps AudioOutputRootPath from filling the disk: deletes capture-output day folders older than
/// AudioRetentionDays (see RetentionPurge), and raises a worker-wide incident when free space drops
/// below MinFreeDiskGb regardless of age — retention is a ceiling, not a promise the drive stays
/// healthy, since AudioOutputRootPath is typically shared with other things on the machine.
///
/// The local disk is a rolling buffer, not the archive: longer-term preservation is a manual
/// FileZilla copy-off, outside this worker's job.
/// </summary>
public sealed class RetentionPurgeWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const double BytesPerGb = 1024.0 * 1024 * 1024;
    // A file that must be archived and is not this long after its last write means the upload is falling behind.
    private static readonly TimeSpan BacklogGrace = TimeSpan.FromHours(2);

    private readonly OperationsWorkerOptions options;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<RetentionPurgeWorker> logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<double?> freeDiskGbReader;
    // Set when archiving is on (see PurgeArchivedFiles): then an .mp4 is deleted only once the
    // archive has verified it.
    private readonly ArchiveLedger? archiveLedger;
    private bool diskWasLow;
    private bool backlogReported;

    public RetentionPurgeWorker(
        OperationsWorkerOptions options,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<RetentionPurgeWorker> logger,
        Func<DateTimeOffset>? clock = null,
        Func<double?>? freeDiskGbReader = null,
        ArchiveLedger? archiveLedger = null)
    {
        this.archiveLedger = archiveLedger;
        this.options = options;
        this.incidentRecorder = incidentRecorder;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.freeDiskGbReader = freeDiskGbReader ?? (() => ReadFreeDiskGb(options.AudioOutputRootPath));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception exception)
            {
                // A failed pass must never take capture down with it — try again next interval.
                logger.LogWarning(exception, "[RetentionPurgeWorker] Purge pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // Internal so a test can drive one pass without waiting on the timer.
    internal void Tick()
    {
        PurgeOldDays();
        CheckDiskSpace();
    }

    private void PurgeOldDays()
    {
        var today = DateOnly.FromDateTime(clock().ToOffset(BogotaOffset).DateTime);
        var cutoff = today.AddDays(-options.AudioRetentionDays);

        if (archiveLedger is not null)
        {
            PurgeArchivedFiles(cutoff, archiveLedger);
            return;
        }

        foreach (var dayDir in RetentionPurge.FindDayFoldersOlderThan(options.AudioOutputRootPath, cutoff))
        {
            try
            {
                Directory.Delete(dayDir, recursive: true);
                logger.LogInformation("[RetentionPurgeWorker] Purged {Dir} (older than {RetentionDays} days).", dayDir, options.AudioRetentionDays);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file still open for writing (an active session mid-rotation) or locked by
                // another process is expected occasionally — it gets picked up on a later pass.
                logger.LogWarning(exception, "[RetentionPurgeWorker] Could not purge {Dir}.", dayDir);
                incidentRecorder.Record(IncidentKinds.WorkerSourceId, IncidentKinds.PurgeFailed, $"{dayDir}: {exception.Message}");
            }
        }
    }

    // Archive mode. What is archived (TV video and the radio's hourly audio, see ArchiveUpload.RequiresArchive)
    // is the only thing whose deletion waits for the archive: such a file from a previous day goes once the
    // ledger has verified its remote copy, and stays (with a backlog incident if it lingers) when it is not
    // there. Everything else — transcriptions, resume files, a TV channel's audio — is simply deleted once
    // its day is over. Today's folder is
    // never part of this (the cutoff is at most today), and a file written to recently is left for a
    // later pass: right after midnight the last hour of the day can still be flushing.
    private void PurgeArchivedFiles(DateOnly cutoff, ArchiveLedger ledger)
    {
        var nowUtc = clock().UtcDateTime;
        var minIdle = TimeSpan.FromMinutes(options.ArchiveMinIdleMinutes);
        var backlog = 0;

        foreach (var dayDir in RetentionPurge.FindDayFoldersOlderThan(options.AudioOutputRootPath, cutoff))
        {
            foreach (var file in new DirectoryInfo(dayDir).EnumerateFiles("*", SearchOption.AllDirectories).ToArray())
            {
                var age = nowUtc - file.LastWriteTimeUtc;
                if (age < minIdle)
                {
                    continue;
                }

                var relative = ArchiveUpload.ToRelativePath(options.AudioOutputRootPath, file.FullName);
                var isArchived = ArchiveUpload.RequiresArchive(relative);
                if (isArchived && !ledger.Contains(relative, file.Length))
                {
                    // Closed but not archived yet: normal while it waits its turn to upload; only a
                    // file that stays that way for hours means the upload is not keeping up.
                    if (age >= BacklogGrace)
                    {
                        backlog++;
                    }

                    continue;
                }

                try
                {
                    file.Delete();
                    if (isArchived)
                    {
                        ledger.Remove(relative);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "[RetentionPurgeWorker] Could not delete {File}.", file.FullName);
                }
            }

            RemoveEmptyDirectories(dayDir);
        }

        if (backlog > 0 && !backlogReported)
        {
            backlogReported = true;
            incidentRecorder.Record(
                IncidentKinds.WorkerSourceId, IncidentKinds.ArchiveBacklog,
                $"{backlog} archivo(s) de días anteriores (video MP4 o audio de radio) llevan más de {BacklogGrace.TotalHours:0} h sin archivar en el servidor; no se borran localmente.");
        }
        else if (backlog == 0)
        {
            backlogReported = false;
        }
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory).ToArray())
        {
            RemoveEmptyDirectories(child);
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for a later pass.
        }
    }

    private void CheckDiskSpace()
    {
        var freeGb = freeDiskGbReader();
        if (freeGb is null)
        {
            return;
        }

        switch (DiskSpaceAlert.Evaluate(diskWasLow, freeGb.Value, options.MinFreeDiskGb))
        {
            case DiskSpaceTransition.BecameLow:
                diskWasLow = true;
                incidentRecorder.Record(
                    IncidentKinds.WorkerSourceId, IncidentKinds.DiskLow,
                    $"Quedan {freeGb:0.#} GB libres en {options.AudioOutputRootPath} (mínimo configurado: {options.MinFreeDiskGb} GB).");
                break;
            case DiskSpaceTransition.Recovered:
                diskWasLow = false;
                incidentRecorder.Record(IncidentKinds.WorkerSourceId, IncidentKinds.DiskRecovered, $"{freeGb:0.#} GB libres en {options.AudioOutputRootPath}.");
                break;
        }
    }

    private static double? ReadFreeDiskGb(string audioOutputRootPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(audioOutputRootPath));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace / BytesPerGb;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
