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

    private readonly OperationsWorkerOptions options;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<RetentionPurgeWorker> logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<double?> freeDiskGbReader;
    // Set when archiving is on: then only files the archive has verified may be deleted.
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

    // Archive mode: never delete what is not verified in the archive. Old files that are not there
    // yet stay put, and a single worker-wide incident says the archive is falling behind.
    private void PurgeArchivedFiles(DateOnly cutoff, ArchiveLedger ledger)
    {
        var backlog = 0;
        foreach (var dayDir in RetentionPurge.FindDayFoldersOlderThan(options.AudioOutputRootPath, cutoff))
        {
            foreach (var file in Directory.EnumerateFiles(dayDir, "*", SearchOption.AllDirectories).ToArray())
            {
                var relative = ArchiveUpload.ToRelativePath(options.AudioOutputRootPath, file);
                if (!ledger.Contains(relative, new FileInfo(file).Length))
                {
                    if (!file.EndsWith(".resume", StringComparison.OrdinalIgnoreCase))
                    {
                        backlog++;
                    }

                    continue;
                }

                try
                {
                    File.Delete(file);
                    ledger.Remove(relative);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "[RetentionPurgeWorker] Could not delete archived file {File}.", file);
                }
            }

            RemoveEmptyDirectories(dayDir);
        }

        if (backlog > 0 && !backlogReported)
        {
            backlogReported = true;
            incidentRecorder.Record(
                IncidentKinds.WorkerSourceId, IncidentKinds.ArchiveBacklog,
                $"{backlog} archivo(s) con más de {options.AudioRetentionDays} días siguen sin archivar en el servidor; no se borran localmente.");
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
