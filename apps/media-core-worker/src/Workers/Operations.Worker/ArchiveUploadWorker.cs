using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Copies closed capture files to the archive (IArchiveStorage) and records each verified upload in
/// the ArchiveLedger — which is what then lets RetentionPurgeWorker delete the local copy.
/// </summary>
public sealed class ArchiveUploadWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly OperationsWorkerOptions options;
    private readonly IArchiveStorage storage;
    private readonly ArchiveLedger ledger;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<ArchiveUploadWorker> logger;
    // Gives each source's UTC offset, which its file names are stamped in. Optional: without it every
    // source is read as Bogota time.
    private readonly ICaptureSourceRepository? sourceRepository;
    private bool failingStreak;

    public ArchiveUploadWorker(
        OperationsWorkerOptions options,
        IArchiveStorage storage,
        ArchiveLedger ledger,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<ArchiveUploadWorker> logger,
        ICaptureSourceRepository? sourceRepository = null)
    {
        this.sourceRepository = sourceRepository;
        this.options = options;
        this.storage = storage;
        this.ledger = ledger;
        this.incidentRecorder = incidentRecorder;
        this.logger = logger;
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
                logger.LogWarning(exception, "[ArchiveUploadWorker] Upload pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyDictionary<string, TimeSpan>?> LoadOffsetsAsync(CancellationToken cancellationToken)
    {
        if (sourceRepository is null)
        {
            return null;
        }

        try
        {
            var sources = await sourceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);
            return sources.ToDictionary(
                source => source.SourceId, source => TimeSpan.FromMinutes(source.UtcOffsetMinutes), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[ArchiveUploadWorker] Could not read source offsets; reading file names as Bogota time.");
            return null;
        }
    }

    // Internal so a test can drive one pass without waiting on the timer.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var pending = ArchiveUpload.FindPendingFiles(
            options.AudioOutputRootPath, ledger.Contains, DateTime.UtcNow, TimeSpan.FromMinutes(options.ArchiveMinIdleMinutes),
            await LoadOffsetsAsync(cancellationToken).ConfigureAwait(false));
        if (pending.Count == 0)
        {
            return;
        }

        logger.LogInformation("[ArchiveUploadWorker] {Count} file(s) pending archive.", pending.Count);

        var uploaded = 0;
        var failed = 0;
        string? firstFailure = null;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ArchiveMaxParallelUploads), CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                if (await storage.UploadAsync(file.LocalPath, file.RelativePath, ct).ConfigureAwait(false))
                {
                    ledger.Add(file.RelativePath, file.Size);
                    Interlocked.Increment(ref uploaded);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    Interlocked.CompareExchange(ref firstFailure, file.RelativePath, null);
                }
            }).ConfigureAwait(false);

        logger.LogInformation("[ArchiveUploadWorker] Archived {Uploaded}, failed {Failed}.", uploaded, failed);

        // One incident per failing streak, not one per pass: a server down overnight would
        // otherwise bury every source's /status row in identical entries.
        if (failed > 0 && uploaded == 0 && !failingStreak)
        {
            failingStreak = true;
            incidentRecorder.Record(
                IncidentKinds.WorkerSourceId, IncidentKinds.ArchiveFailed,
                $"No se pudo archivar en el servidor ({failed} archivo(s) pendientes, ej. {firstFailure}).");
        }
        else if (uploaded > 0)
        {
            failingStreak = false;
        }
    }
}
