using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Body of POST /maintenance/radio-opus/convert: a trial run keeps its result in a preview folder, and a limit takes only that many files.</summary>
public sealed class RadioOpusConvertRequest
{
    public bool DryRun { get; set; }

    public int? Limit { get; set; }

    /// <summary>How many files are re-encoded at once in this run; empty means the number in the options.</summary>
    public int? Parallel { get; set; }
}

/// <summary>Where the one-time conversion stands. State is idle, running or finished.</summary>
public sealed record RadioOpusConversionStatus(
    string State,
    bool DryRun,
    int Parallel,
    int Total,
    int Converted,
    int Skipped,
    int Failed,
    int Deferred,
    long BytesBefore,
    long BytesAfter,
    double AverageSecondsPerFile,
    double? EstimatedMinutesLeft,
    IReadOnlyList<string> Current,
    double? RecentClosedHoursAverageMb,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string PreviewDirectory);

/// <summary>
/// The one-time conversion of the radio hours already on disk to the bitrate the capture now records at (32 kbps, mono,
/// 16 kHz), run from the service when asked (see the /maintenance/radio-opus routes) and never on its own. It runs in
/// the service, not as a separate tool, because the archive ledger is kept by the service: a converted file changes
/// size, and the ledger knows what was archived by path AND size, so a tool outside would make the archive upload
/// every converted file again.
///
/// Per file: re-encode to "&lt;file&gt;.convert.tmp" beside it, check it (not empty, smaller, same duration), tell the
/// archive ledger the new size if the file was archived, and replace the file atomically. A file somebody has open
/// (the alert editor reading it) cannot be replaced: it is left for the next run. Nothing here is left on afterwards.
/// A trial run keeps the result in a preview folder and touches nothing else.
/// </summary>
public sealed class RadioOpusConversionJob
{
    // A file already at or under this average bitrate was recorded (or converted) with the new quality.
    private const double AlreadyLowKbps = 40;

    /// <summary>The most files re-encoded at once: the machine has 16 logical processors and the capture needs its share.</summary>
    public const int MaxParallel = 16;
    private static readonly TimeSpan StaleTemporary = TimeSpan.FromHours(1);
    private static readonly Meter ConversionMeter = new("MediaOpsCore.Workers.Operations.RadioOpusConversion", "1.0.0");
    private static readonly Counter<long> FilesConverted = ConversionMeter.CreateCounter<long>("radio_opus_converted_files");
    private static readonly Counter<long> BytesSaved = ConversionMeter.CreateCounter<long>("radio_opus_bytes_saved");

    private readonly OperationsWorkerOptions options;
    private readonly ArchiveLedger? archiveLedger;
    private readonly ArchiveLedger convertedLedger;
    private readonly ILogger<RadioOpusConversionJob> logger;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, TimeSpan>?>> offsets;
    private readonly Action<string, string> reencode;
    private readonly Func<string, OggOpusInfo?> readInfo;
    private readonly long minBytes;
    private readonly Func<DateTime> clock;

    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private string state = "idle";
    private bool dryRun;
    private int parallel;
    private int total, converted, skipped, failed, deferred;
    private long bytesBefore, bytesAfter;
    private double processedSeconds;
    private int processedFiles;
    private readonly ConcurrentDictionary<string, byte> current = new();
    private DateTimeOffset? startedAt, finishedAt;

    public RadioOpusConversionJob(
        OperationsWorkerOptions options,
        ArchiveLedger? archiveLedger,
        ArchiveLedger convertedLedger,
        ILogger<RadioOpusConversionJob> logger,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, TimeSpan>?>>? offsets = null,
        Action<string, string>? reencode = null,
        Func<string, OggOpusInfo?>? readInfo = null,
        long? minBytes = null,
        Func<DateTime>? clock = null)
    {
        this.options = options;
        this.archiveLedger = archiveLedger;
        this.convertedLedger = convertedLedger;
        this.logger = logger;
        this.offsets = offsets ?? (_ => Task.FromResult<IReadOnlyDictionary<string, TimeSpan>?>(null));
        this.reencode = reencode ?? ((input, output) =>
            OpusSegmentTranscoder.TranscodeFileToOpus(input, output, options.RadioOpusBitrateKbps, SpeechAudio.SampleRate));
        this.readInfo = readInfo ?? OggOpusInfo.Read;
        this.minBytes = minBytes ?? RadioOpusConversion.DefaultMinBytes;
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    private string PreviewDirectory => Path.Combine(options.StageFilesystemRootPath, "radio-opus-preview");

    public RadioOpusConversionStatus Status
    {
        get
        {
            lock (gate)
            {
                var average = processedFiles == 0 ? 0 : processedSeconds / processedFiles;
                var pending = Math.Max(0, total - converted - skipped - failed - deferred);
                double? left = state == "running" && processedFiles > 0
                    ? pending * average / Math.Max(1, parallel) / 60
                    : null;
                return new RadioOpusConversionStatus(
                    state, dryRun, parallel, total, converted, skipped, failed, deferred, bytesBefore, bytesAfter,
                    Math.Round(average, 1), left is null ? null : Math.Round(left.Value, 0), current.Keys.Order().ToArray(),
                    RadioOpusConversion.RecentClosedHoursAverageMb(
                        options.AudioOutputRootPath, clock(), TimeSpan.FromMinutes(options.ArchiveMinIdleMinutes), null, count: 24),
                    startedAt, finishedAt, PreviewDirectory);
            }
        }
    }

    /// <summary>Starts a run in the background. False when one is already running.</summary>
    public bool TryStart(bool dryRun, int? limit, int? parallel = null)
    {
        if (!TryBegin(dryRun, parallel, out var token))
        {
            return false;
        }

        _ = Task.Run(() => RunCoreAsync(dryRun, limit, token));
        return true;
    }

    /// <summary>Runs to the end (or until stopped). Throws when a run is already going.</summary>
    public Task RunAsync(bool dryRun, int? limit, CancellationToken cancellationToken, int? parallel = null)
    {
        if (!TryBegin(dryRun, parallel, out var token))
        {
            throw new InvalidOperationException("The conversion is already running.");
        }

        return RunCoreAsync(dryRun, limit, token, cancellationToken);
    }

    /// <summary>Lets the files in hand finish and takes no more.</summary>
    public void Stop()
    {
        lock (gate)
        {
            cancellation?.Cancel();
        }
    }

    public async Task WaitUntilFinishedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Status.State == "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private bool TryBegin(bool isDryRun, int? requestedParallel, out CancellationToken token)
    {
        lock (gate)
        {
            token = default;
            if (state == "running")
            {
                return false;
            }

            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            token = cancellation.Token;
            state = "running";
            dryRun = isDryRun;
            parallel = Math.Clamp(requestedParallel is > 0 ? requestedParallel.Value : options.RadioOpusConversionMaxParallel, 1, MaxParallel);
            total = converted = skipped = failed = deferred = processedFiles = 0;
            bytesBefore = bytesAfter = 0;
            processedSeconds = 0;
            current.Clear();
            startedAt = DateTimeOffset.UtcNow;
            finishedAt = null;
            return true;
        }
    }

    private async Task RunCoreAsync(bool isDryRun, int? limit, CancellationToken stopToken, CancellationToken callerToken = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, callerToken);
            IReadOnlyDictionary<string, TimeSpan>? sourceOffsets = null;
            try
            {
                sourceOffsets = await offsets(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[RadioOpusConversion] Could not read the sources; reading file names as Bogota time.");
            }

            ClearStaleTemporaryFiles();

            var candidates = RadioOpusConversion.FindCandidates(
                options.AudioOutputRootPath,
                convertedLedger.Contains,
                clock(),
                TimeSpan.FromMinutes(options.ArchiveMinIdleMinutes),
                sourceOffsets,
                minBytes);
            if (limit is > 0)
            {
                candidates = candidates.Take(limit.Value).ToArray();
            }

            lock (gate)
            {
                total = candidates.Count;
            }

            logger.LogInformation(
                "[RadioOpusConversion] {Mode}: {Count} radio hour file(s) to convert to {Kbps} kbps, {Parallel} at a time.",
                isDryRun ? "Trial run" : "Converting", candidates.Count, options.RadioOpusBitrateKbps, Status.Parallel);

            var queue = new ConcurrentQueue<RadioOpusCandidate>(candidates);
            var threads = Enumerable.Range(0, Math.Max(1, Status.Parallel))
                .Select(_ => new Thread(() => Work(queue, isDryRun, linked.Token))
                {
                    IsBackground = true,
                    // The capture decodes 46 streams in this same process: the conversion must not starve it.
                    Priority = ThreadPriority.BelowNormal,
                })
                .ToArray();
            foreach (var thread in threads)
            {
                thread.Start();
            }

            await Task.Run(() =>
            {
                foreach (var thread in threads)
                {
                    thread.Join();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[RadioOpusConversion] The conversion stopped because of an error.");
        }
        finally
        {
            lock (gate)
            {
                state = "finished";
                finishedAt = DateTimeOffset.UtcNow;
            }

            var status = Status;
            logger.LogInformation(
                "[RadioOpusConversion] Finished: {Converted} converted, {Skipped} already small, {Failed} failed, {Deferred} left for later; {Before} MB → {After} MB.",
                status.Converted, status.Skipped, status.Failed, status.Deferred, status.BytesBefore / 1_000_000, status.BytesAfter / 1_000_000);
        }
    }

    private void Work(ConcurrentQueue<RadioOpusCandidate> queue, bool isDryRun, CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested && queue.TryDequeue(out var candidate))
        {
            var clockStart = DateTime.UtcNow;
            current[candidate.RelativePath] = 0;
            try
            {
                Convert(candidate, isDryRun);
            }
            finally
            {
                current.TryRemove(candidate.RelativePath, out _);
                lock (gate)
                {
                    processedFiles++;
                    processedSeconds += (DateTime.UtcNow - clockStart).TotalSeconds;
                }
            }
        }
    }

    private void Convert(RadioOpusCandidate candidate, bool isDryRun)
    {
        string? temporary = null;
        try
        {
            var info = readInfo(candidate.Path);
            if (info is null)
            {
                Count(ref failed);
                logger.LogWarning("[RadioOpusConversion] {Path} is not a readable Ogg Opus file; left as it is.", candidate.RelativePath);
                return;
            }

            if (info.AverageKbps(candidate.Size) <= AlreadyLowKbps)
            {
                Count(ref skipped);
                return;
            }

            if (isDryRun)
            {
                Directory.CreateDirectory(PreviewDirectory);
            }

            temporary = isDryRun ? Path.Combine(PreviewDirectory, Path.GetFileName(candidate.Path)) : candidate.Path + ".convert.tmp";
            reencode(candidate.Path, temporary);

            var result = readInfo(temporary);
            var newSize = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
            if (result is null || newSize <= 0 || newSize >= candidate.Size
                || Math.Abs((result.Duration - info.Duration).TotalSeconds) > 1)
            {
                Count(ref failed);
                logger.LogWarning("[RadioOpusConversion] The new version of {Path} is not valid (size {Size}, duration {Duration}); kept the original.",
                    candidate.RelativePath, newSize, result?.Duration);
                DeleteQuietly(temporary);
                return;
            }

            if (isDryRun)
            {
                Record(candidate.Size, newSize);
                temporary = null; // the preview stays for listening
                return;
            }

            // The archive knows this file by path and size: tell it the new size before the file changes, so it never
            // sees a "different" file and uploads it again. Put back if the replacement fails.
            var archived = archiveLedger?.Contains(candidate.RelativePath, candidate.Size) == true;
            if (archived)
            {
                archiveLedger!.Add(candidate.RelativePath, newSize);
            }

            try
            {
                File.Move(temporary, candidate.Path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (archived)
                {
                    archiveLedger!.Add(candidate.RelativePath, candidate.Size);
                }

                Count(ref deferred);
                logger.LogInformation("[RadioOpusConversion] {Path} is in use; left for the next run ({Reason}).", candidate.RelativePath, exception.Message);
                return;
            }

            temporary = null;
            convertedLedger.Add(candidate.RelativePath, newSize);
            Record(candidate.Size, newSize);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Count(ref failed);
            logger.LogWarning(exception, "[RadioOpusConversion] Could not convert {Path}; kept the original.", candidate.RelativePath);
        }
        finally
        {
            if (temporary is not null)
            {
                DeleteQuietly(temporary);
            }
        }
    }

    private void Record(long before, long after)
    {
        lock (gate)
        {
            converted++;
            bytesBefore += before;
            bytesAfter += after;
        }

        FilesConverted.Add(1);
        BytesSaved.Add(before - after);
    }

    private void Count(ref int counter)
    {
        lock (gate)
        {
            counter++;
        }
    }

    // What an earlier run left half done (the service stopped in the middle of a file). A recent one may be in use.
    private void ClearStaleTemporaryFiles()
    {
        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(options.AudioOutputRootPath))
        {
            try
            {
                foreach (var file in new DirectoryInfo(dayDir).EnumerateFiles("*.convert.tmp", SearchOption.AllDirectories).ToArray())
                {
                    if (clock() - file.LastWriteTimeUtc > StaleTemporary)
                    {
                        DeleteQuietly(file.FullName);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "[RadioOpusConversion] Could not look for leftovers in {Dir}.", dayDir);
            }
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "[RadioOpusConversion] Could not delete {Path}.", path);
        }
    }
}
