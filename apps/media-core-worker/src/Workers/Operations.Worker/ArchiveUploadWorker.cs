using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
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

    // How many closed recordings are rewritten as regular MP4 in one pass: each is read and written whole, so the
    // backlog (every hour recorded before this existed) is worked off over several passes instead of one long stall.
    internal const int MaxFinalizedPerPass = 40;

    private readonly OperationsWorkerOptions options;
    private readonly IArchiveStorageProvider storages;
    private readonly ArchiveLedger ledger;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<ArchiveUploadWorker> logger;
    // Gives each source's UTC offset, which its file names are stamped in. Optional: without it every
    // source is read as Bogota time.
    private readonly ICaptureSourceRepository? sourceRepository;
    // Rewrites a radio hour (opus) into the small mono opus that is archived; injectable so a pass can be tested
    // without FFmpeg.
    private readonly Func<string, string, CancellationToken, Task> reencode;
    // Rewrites a closed fragmented MP4 as a regular one (see Mp4Finalizer); false when there was nothing to do.
    private readonly Func<string, CancellationToken, bool> finalize;
    private bool failingStreak;

    public ArchiveUploadWorker(
        OperationsWorkerOptions options,
        IArchiveStorageProvider storages,
        ArchiveLedger ledger,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<ArchiveUploadWorker> logger,
        ICaptureSourceRepository? sourceRepository = null,
        Func<string, string, CancellationToken, Task>? reencode = null,
        Func<string, CancellationToken, bool>? finalize = null)
    {
        this.finalize = finalize ?? Mp4Finalizer.Finalize;
        this.sourceRepository = sourceRepository;
        this.reencode = reencode ?? ((input, output, ct) => Task.Run(
            () => OpusSegmentTranscoder.TranscodeFileToOpus(input, output, options.ArchiveRadioKbps, options.ArchiveRadioSampleRateHz), ct));
        this.options = options;
        this.storages = storages;
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

    // The catalog's sources: each one's UTC offset (its file names are stamped in it) and country (its folder in the
    // backup). Without them every source is read as Bogota time and filed under the default country.
    private async Task<IReadOnlyList<CaptureSource>?> LoadSourcesAsync(CancellationToken cancellationToken)
    {
        if (sourceRepository is null)
        {
            return null;
        }

        try
        {
            return await sourceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[ArchiveUploadWorker] Could not read the sources; reading file names as Bogota time, filing under {Country}.", ArchiveLayout.DefaultCountry);
            return null;
        }
    }

    // The account of the file's medium. Video outside the radio and notes folders is TV: that is all the
    // archive ever takes besides radio.
    private IArchiveStorage? StorageFor(PendingArchiveFile file) =>
        storages.For(ArchiveMedia.MediumOf(file.RelativePath) ?? ArchiveMedia.Television);

    // Uploads one file (re-encoding a radio hour smaller first). False — never an exception — for an operational
    // failure, so one corrupt file cannot stop the others and is simply retried on the next pass.
    private async Task<bool> ArchiveOneAsync(PendingArchiveFile file, CancellationToken cancellationToken)
    {
        var storage = StorageFor(file)!;
        if (!file.Reencode)
        {
            return await storage.UploadAsync(file.LocalPath, file.RemoteRelativePath, cancellationToken).ConfigureAwait(false);
        }

        var temporaryFile = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}.opus");
        try
        {
            await reencode(file.LocalPath, temporaryFile, cancellationToken).ConfigureAwait(false);
            return await storage.UploadAsync(temporaryFile, file.RemoteRelativePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[ArchiveUploadWorker] Could not re-encode {Path} for the archive.", file.RelativePath);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryFile);
            }
            catch (IOException)
            {
                // A leftover temp file is cleaned with the temp folder.
            }
        }
    }

    // Closed TV recordings (the same "closed" the upload uses) that are still fragmented, oldest first.
    private Task FinalizeClosedRecordingsAsync(
        IReadOnlyDictionary<string, TimeSpan>? offsets, Func<string, string?> countryOf, TimeSpan minIdle, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var closed = ArchiveUpload.FindPendingFiles(options.AudioOutputRootPath, (_, _) => false, DateTime.UtcNow, minIdle, offsets, countryOf)
                .Where(file => file.LocalPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

            var converted = 0;
            foreach (var file in closed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (finalize(file.LocalPath, cancellationToken) && ++converted >= MaxFinalizedPerPass)
                    {
                        break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // It is archived as it is; the next pass tries again.
                    logger.LogWarning(exception, "[ArchiveUploadWorker] Could not rewrite {Path} as a regular MP4.", file.RelativePath);
                }
            }

            if (converted > 0)
            {
                logger.LogInformation("[ArchiveUploadWorker] Rewrote {Count} closed recording(s) as regular MP4 so they can be sought.", converted);
            }
        }, cancellationToken);

    // Internal so a test can drive one pass without waiting on the timer.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // A medium with no account is left alone here: nothing is uploaded or ledgered for it, so the purge keeps its
        // files, and its backlog incident is what says they are waiting for an account.
        var sources = await LoadSourcesAsync(cancellationToken).ConfigureAwait(false);
        var offsets = sources?.ToDictionary(source => source.SourceId, source => TimeSpan.FromMinutes(source.UtcOffsetMinutes), StringComparer.OrdinalIgnoreCase);
        var countries = sources?.ToDictionary(source => source.SourceId, source => source.Country, StringComparer.OrdinalIgnoreCase);
        var minIdle = TimeSpan.FromMinutes(options.ArchiveMinIdleMinutes);
        Func<string, string?> countryOf = sourceId => countries is not null && countries.TryGetValue(sourceId, out var country) ? country : null;

        // Before looking at what is pending: a rewritten file has a new size, so the ledger no longer knows it and it is
        // archived again — the copy already in the archive, which cannot be sought either, is replaced.
        await FinalizeClosedRecordingsAsync(offsets, countryOf, minIdle, cancellationToken).ConfigureAwait(false);

        var pending = ArchiveUpload.FindPendingFiles(
            options.AudioOutputRootPath, ledger.Contains, DateTime.UtcNow, minIdle, offsets, countryOf)
            .Where(file => StorageFor(file) is not null)
            .ToList();
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
                if (await ArchiveOneAsync(file, ct).ConfigureAwait(false))
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
