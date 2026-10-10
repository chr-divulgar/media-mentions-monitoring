using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public abstract record VideoSegmentResult;

/// <summary>A clip cut into a temp file the caller must delete once it has sent it.</summary>
public sealed record VideoSegmentSuccess(string TempPath, DateTimeOffset ClipStartUtc, double DurationSeconds) : VideoSegmentResult;

public sealed record VideoSegmentNotAvailable(string Reason) : VideoSegmentResult;

/// <summary>
/// Cuts a window of a TV channel's recording (RQ-011) for the alert editor: finds the hourly MP4 that
/// holds the window — see TvRecordingLocator — and cuts it with Mp4SegmentCutter. The window is
/// clamped to that one recording (it never joins two files), and the clip's real start is returned
/// because stream copy begins on a keyframe, a little before the start that was asked for.
/// A recording that is no longer on this disk (the purge keeps only today's) is fetched back from the
/// archive into a size- and age-capped cache folder, then cut like any other.
/// </summary>
public sealed class VideoSegmentService
{
    // Bogota, where every source of this deployment is: used when the source is no longer in the active
    // catalog (an excluded channel still has recordings).
    private static readonly TimeSpan FallbackOffset = TimeSpan.FromHours(-5);
    private const string TelevisionDirectory = "television";

    // A cut reads a whole hourly file; two at a time keeps the disk from thrashing under a few editors.
    private static readonly SemaphoreSlim CutGate = new(2, 2);

    private readonly OperationsWorkerOptions options;
    private readonly ICaptureSourceProvider sourceProvider;
    private readonly ILogger<VideoSegmentService> logger;
    private readonly IArchiveStorage? archive;
    private readonly ArchiveDownloadCache? cache;

    public VideoSegmentService(
        OperationsWorkerOptions options, ICaptureSourceProvider sourceProvider, ILogger<VideoSegmentService> logger, IArchiveStorage? archive = null)
    {
        this.options = options;
        this.sourceProvider = sourceProvider;
        this.logger = logger;
        this.archive = archive;
        cache = archive is null
            ? null
            : new ArchiveDownloadCache(
                archive,
                Path.Combine(options.StageFilesystemRootPath, "video-cache"),
                TimeSpan.FromHours(options.VideoCacheHours),
                options.VideoCacheMaxGb * 1024L * 1024 * 1024,
                logger);
    }

    private string CacheRoot => Path.Combine(options.StageFilesystemRootPath, "video-cache");

    public async Task<VideoSegmentResult> GetSegmentAsync(
        string sourceId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var offset = SourceOffset(sourceId);
        var windowStart = startUtc.ToOffset(offset);
        var windowEnd = endUtc.ToOffset(offset);

        // The recording that holds the middle of the window; if that falls in a gap, either end.
        var middle = windowStart + (windowEnd - windowStart) / 2;
        var directories = DayDirectories(sourceId, windowStart, windowEnd);
        var inputRoot = options.AudioOutputRootPath;
        var recording = Locate(ListLocalRecordings(directories, sourceId, offset), middle, windowStart, windowEnd);
        if (recording is null && archive is not null)
        {
            List<TvRecordingCandidate> archived;
            try
            {
                archived = await ListArchivedRecordingsAsync(directories, sourceId, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[VideoSegmentService] Could not list the archive for {Source}.", sourceId);
                return new VideoSegmentNotAvailable("No se pudo consultar el archivo de videos. Intente de nuevo en unos minutos.");
            }

            recording = Locate(archived, middle, windowStart, windowEnd);
            if (recording is not null)
            {
                if (!await cache!.EnsureAsync(recording.RelativePath, cancellationToken).ConfigureAwait(false))
                {
                    return new VideoSegmentNotAvailable("No se pudo descargar el video del archivo. Intente de nuevo en unos minutos.");
                }

                inputRoot = CacheRoot;
            }
        }

        if (recording is null)
        {
            return new VideoSegmentNotAvailable($"No hay video grabado de '{sourceId}' para ese momento.");
        }

        var (startSeconds, endSeconds) = TvRecordingLocator.ClipOffsets(recording, windowStart, windowEnd);
        var inputPath = Path.Combine(inputRoot, recording.RelativePath);
        var outputPath = Path.Combine(Path.GetTempPath(), $"video-segment-{Guid.NewGuid():N}.mp4");

        await CutGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var clip = await Task.Run(
                () => Mp4SegmentCutter.Cut(inputPath, startSeconds, endSeconds, outputPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (clip is null)
            {
                return new VideoSegmentNotAvailable($"El video de '{sourceId}' no llega hasta ese momento (la grabación se interrumpió antes).");
            }

            return new VideoSegmentSuccess(
                outputPath, (recording.Start + TimeSpan.FromSeconds(clip.StartSeconds)).ToUniversalTime(), clip.DurationSeconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[VideoSegmentService] Could not cut {Input}.", inputPath);
            TryDelete(outputPath);
            return new VideoSegmentNotAvailable("No se pudo leer el video grabado.");
        }
        finally
        {
            CutGate.Release();
        }
    }

    private TimeSpan SourceOffset(string sourceId)
    {
        var source = sourceProvider.ListActiveSourcesAsync().GetAwaiter().GetResult()
            .FirstOrDefault(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        return source is null ? FallbackOffset : TimeSpan.FromMinutes(source.UtcOffsetMinutes);
    }

    // The recording that holds the middle of the window; if that falls in a gap, either end.
    private static TvRecordingCandidate? Locate(
        List<TvRecordingCandidate> candidates, DateTimeOffset middle, DateTimeOffset windowStart, DateTimeOffset windowEnd) =>
        TvRecordingLocator.Find(candidates, middle)
            ?? TvRecordingLocator.Find(candidates, windowEnd)
            ?? TvRecordingLocator.Find(candidates, windowStart);

    // The folders of the days the window touches plus the day before the first: a recording that
    // started in the last minute of a day runs into the next one, and sits in the earlier day's folder.
    private static List<string> DayDirectories(string sourceId, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var directories = new List<string>();
        for (var day = DateOnly.FromDateTime(windowStart.DateTime).AddDays(-1); day <= DateOnly.FromDateTime(windowEnd.DateTime); day = day.AddDays(1))
        {
            directories.Add(string.Join('/', TelevisionDirectory, day.Year.ToString("D4"), day.Month.ToString("D2"), day.Day.ToString("D2"), sourceId));
        }

        return directories;
    }

    private static void AddCandidates(
        List<TvRecordingCandidate> candidates, string relativeDirectory, IEnumerable<string> fileNames, string sourceId, TimeSpan offset)
    {
        foreach (var name in fileNames)
        {
            if (TvRecordingLocator.TryParseStart(name, sourceId, offset, out var start))
            {
                candidates.Add(new TvRecordingCandidate($"{relativeDirectory}/{name}", start));
            }
        }
    }

    private List<TvRecordingCandidate> ListLocalRecordings(List<string> directories, string sourceId, TimeSpan offset)
    {
        var candidates = new List<TvRecordingCandidate>();
        foreach (var relativeDirectory in directories)
        {
            var directory = Path.Combine(options.AudioOutputRootPath, relativeDirectory);
            if (Directory.Exists(directory))
            {
                AddCandidates(candidates, relativeDirectory, Directory.EnumerateFiles(directory, "*.mp4").Select(file => Path.GetFileName(file)), sourceId, offset);
            }
        }

        return candidates;
    }

    private async Task<List<TvRecordingCandidate>> ListArchivedRecordingsAsync(
        List<string> directories, string sourceId, TimeSpan offset, CancellationToken cancellationToken)
    {
        var candidates = new List<TvRecordingCandidate>();
        foreach (var relativeDirectory in directories)
        {
            var names = await archive!.ListAsync(relativeDirectory, cancellationToken).ConfigureAwait(false);
            AddCandidates(candidates, relativeDirectory, names, sourceId, offset);
        }

        return candidates;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is cleaned with the temp folder.
        }
    }
}
