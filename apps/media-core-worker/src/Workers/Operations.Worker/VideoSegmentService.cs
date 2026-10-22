using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
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

    // A window the local disk does not cover at its start is looked up in the archive as well (yesterday's last hour is
    // only there now that the disk keeps just today's).
    private static readonly TimeSpan CoverageTolerance = TimeSpan.FromMinutes(1);

    // One recording's part of the window, with where its file is to be read.
    private sealed record Piece(TvRecordingSlice Slice, string InputRoot);

    private sealed record Cutting(string Path, DateTimeOffset StartUtc, double DurationSeconds);

    /// <summary>
    /// The window as one MP4. A window inside one recording is cut from it; one that crosses into the next (or the
    /// previous) recording is cut from each and joined, every clip at its real time. exact makes the clip start and end
    /// on the very instants asked (the video is encoded again from the keyframe); otherwise it begins on the keyframe
    /// before the start, and the real start is returned.
    /// </summary>
    public async Task<VideoSegmentResult> GetSegmentAsync(
        string sourceId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken, bool exact = false)
    {
        var source = FindSource(sourceId);
        var offset = source is null ? FallbackOffset : TimeSpan.FromMinutes(source.UtcOffsetMinutes);
        var windowStart = startUtc.ToOffset(offset);
        var windowEnd = endUtc.ToOffset(offset);

        var directories = DayDirectories(sourceId, windowStart, windowEnd);
        var local = ListLocalRecordings(directories, sourceId, offset);
        var slices = TvRecordingLocator.FindOverlapping(local, windowStart, windowEnd);

        if (archive is not null && (slices.Count == 0 || slices[0].From > windowStart + CoverageTolerance))
        {
            try
            {
                var archived = await ListArchivedRecordingsAsync(
                    windowStart, windowEnd, directories, sourceId, source?.Country, offset, cancellationToken).ConfigureAwait(false);
                // What is on this disk is read from it; the archive adds only the recordings that are not here.
                var merged = local.Concat(archived.Where(candidate => local.All(have => have.Start != candidate.Start))).ToList();
                slices = TvRecordingLocator.FindOverlapping(merged, windowStart, windowEnd);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[VideoSegmentService] Could not list the archive for {Source}.", sourceId);
                if (slices.Count == 0)
                {
                    return new VideoSegmentNotAvailable("No se pudo consultar el archivo de videos. Intente de nuevo en unos minutos.");
                }
            }
        }

        if (slices.Count == 0)
        {
            return new VideoSegmentNotAvailable($"No hay video grabado de '{sourceId}' para ese momento.");
        }

        var pieces = new List<Piece>();
        foreach (var slice in slices)
        {
            if (!slice.Recording.Archived)
            {
                pieces.Add(new Piece(slice, options.AudioOutputRootPath));
            }
            else if (await cache!.EnsureAsync(slice.Recording.RelativePath, cancellationToken).ConfigureAwait(false))
            {
                pieces.Add(new Piece(slice, CacheRoot));
            }
            else if (slices.Count == 1)
            {
                return new VideoSegmentNotAvailable("No se pudo descargar el video del archivo. Intente de nuevo en unos minutos.");
            }
            else
            {
                logger.LogWarning("[VideoSegmentService] Could not fetch {Recording}; the clip goes without that part.", slice.Recording.RelativePath);
            }
        }

        if (pieces.Count == 0)
        {
            return new VideoSegmentNotAvailable("No se pudo descargar el video del archivo. Intente de nuevo en unos minutos.");
        }

        await CutGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaries = new List<string>();
        try
        {
            // One piece: cut it as asked (exact too). Several: copy each part, join them, and — when exact — trim the
            // joined clip, which is a plain MP4 that starts at the first part.
            var cuttings = new List<Cutting>();
            foreach (var piece in pieces)
            {
                var cutting = await Task.Run(
                    () => CutPiece(piece, exact && pieces.Count == 1, temporaries, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (cutting is not null)
                {
                    cuttings.Add(cutting);
                }
            }

            if (cuttings.Count == 0)
            {
                return new VideoSegmentNotAvailable($"El video de '{sourceId}' no llega hasta ese momento (la grabación se interrumpió antes).");
            }

            var result = cuttings.Count == 1
                ? await SingleAsync(cuttings[0], pieces, exact && pieces.Count > 1, windowStart, windowEnd, temporaries, cancellationToken).ConfigureAwait(false)
                : await JoinAsync(cuttings, exact, windowStart, windowEnd, temporaries, cancellationToken).ConfigureAwait(false);

            // Everything but the clip that is handed back goes.
            foreach (var path in temporaries.Where(path => result is not VideoSegmentSuccess success || path != success.TempPath))
            {
                TryDelete(path);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[VideoSegmentService] Could not cut the window of {Source}.", sourceId);
            foreach (var path in temporaries)
            {
                TryDelete(path);
            }

            return new VideoSegmentNotAvailable("No se pudo leer el video grabado.");
        }
        finally
        {
            CutGate.Release();
        }
    }

    // The part of the window in one recording, cut out of its file into a temp file; null when the recording does not
    // reach that far (it dropped before). Exact: encoded again to start on the asked picture — and, if that fails, copied.
    private Cutting? CutPiece(Piece piece, bool exact, List<string> temporaries, CancellationToken cancellationToken)
    {
        var recording = piece.Slice.Recording;
        var startSeconds = (piece.Slice.From - recording.Start).TotalSeconds;
        var endSeconds = (piece.Slice.To - recording.Start).TotalSeconds;
        var inputPath = Path.Combine(piece.InputRoot, recording.RelativePath);
        var outputPath = Path.Combine(Path.GetTempPath(), $"video-segment-{Guid.NewGuid():N}.mp4");
        temporaries.Add(outputPath);

        var clip = CutOrCopy(inputPath, startSeconds, endSeconds, outputPath, exact, cancellationToken);
        return clip is null
            ? null
            : new Cutting(outputPath, (recording.Start + TimeSpan.FromSeconds(clip.StartSeconds)).ToUniversalTime(), clip.DurationSeconds);
    }

    private Mp4ClipResult? CutOrCopy(
        string inputPath, double startSeconds, double endSeconds, string outputPath, bool exact, CancellationToken cancellationToken)
    {
        if (!exact)
        {
            return Mp4SegmentCutter.Cut(inputPath, startSeconds, endSeconds, outputPath, cancellationToken);
        }

        try
        {
            return Mp4SegmentCutter.Cut(inputPath, startSeconds, endSeconds, outputPath, cancellationToken, exact: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[VideoSegmentService] Could not cut {Input} exactly; copying it from the keyframe instead.", inputPath);
            TryDelete(outputPath);
            return Mp4SegmentCutter.Cut(inputPath, startSeconds, endSeconds, outputPath, cancellationToken);
        }
    }

    // One part came out of several (the others did not reach the window): it is the clip, trimmed exactly if asked.
    private async Task<VideoSegmentResult> SingleAsync(
        Cutting cutting, List<Piece> pieces, bool needsExactTrim, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        List<string> temporaries, CancellationToken cancellationToken)
    {
        if (!needsExactTrim)
        {
            return new VideoSegmentSuccess(cutting.Path, cutting.StartUtc, cutting.DurationSeconds);
        }

        return await TrimExactAsync(cutting, windowStart, windowEnd, temporaries, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VideoSegmentResult> JoinAsync(
        List<Cutting> cuttings, bool exact, DateTimeOffset windowStart, DateTimeOffset windowEnd, List<string> temporaries,
        CancellationToken cancellationToken)
    {
        var first = cuttings[0];
        var joinedPath = Path.Combine(Path.GetTempPath(), $"video-segment-{Guid.NewGuid():N}.mp4");
        temporaries.Add(joinedPath);

        var parts = cuttings.Select(cutting => (cutting.Path, (cutting.StartUtc - first.StartUtc).TotalSeconds)).ToList();
        var joined = await Task.Run(() => Mp4Joiner.TryJoin(parts, joinedPath), cancellationToken).ConfigureAwait(false);
        if (!joined)
        {
            // Parts that do not match (the day the picture went from 240p to 480p) cannot be one file: the clip is the part
            // that holds the middle of the window, as before windows could cross recordings.
            var middle = windowStart + (windowEnd - windowStart) / 2;
            var chosen = cuttings.FirstOrDefault(cutting => cutting.StartUtc <= middle && middle <= cutting.StartUtc.AddSeconds(cutting.DurationSeconds)) ?? first;
            logger.LogWarning("[VideoSegmentService] The recordings of the window do not match; using only the one with its middle.");
            return exact
                ? await TrimExactAsync(chosen, windowStart, windowEnd, temporaries, cancellationToken).ConfigureAwait(false)
                : new VideoSegmentSuccess(chosen.Path, chosen.StartUtc, chosen.DurationSeconds);
        }

        var last = cuttings[^1];
        var whole = new Cutting(joinedPath, first.StartUtc, (last.StartUtc - first.StartUtc).TotalSeconds + last.DurationSeconds);
        return exact
            ? await TrimExactAsync(whole, windowStart, windowEnd, temporaries, cancellationToken).ConfigureAwait(false)
            : new VideoSegmentSuccess(whole.Path, whole.StartUtc, whole.DurationSeconds);
    }

    // The window cut again, exactly, out of a clip that already holds it (a plain MP4 that begins at cutting.StartUtc).
    private async Task<VideoSegmentResult> TrimExactAsync(
        Cutting cutting, DateTimeOffset windowStart, DateTimeOffset windowEnd, List<string> temporaries, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"video-segment-{Guid.NewGuid():N}.mp4");
        temporaries.Add(outputPath);
        var from = Math.Max(0, (windowStart - cutting.StartUtc).TotalSeconds);
        var to = Math.Max(from, (windowEnd - cutting.StartUtc).TotalSeconds);

        var clip = await Task.Run(() => CutOrCopy(cutting.Path, from, to, outputPath, exact: true, cancellationToken), cancellationToken).ConfigureAwait(false);
        return clip is null
            ? new VideoSegmentSuccess(cutting.Path, cutting.StartUtc, cutting.DurationSeconds)
            : new VideoSegmentSuccess(outputPath, cutting.StartUtc.AddSeconds(clip.StartSeconds), clip.DurationSeconds);
    }

    private CaptureSource? FindSource(string sourceId) =>
        sourceProvider.ListActiveSourcesAsync().GetAwaiter().GetResult()
            .FirstOrDefault(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

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
        List<TvRecordingCandidate> candidates, string relativeDirectory, IEnumerable<string> fileNames, string sourceId, TimeSpan offset,
        bool archived = false)
    {
        foreach (var name in fileNames)
        {
            if (TvRecordingLocator.TryParseStart(name, sourceId, offset, out var start))
            {
                candidates.Add(new TvRecordingCandidate($"{relativeDirectory}/{name}", start, archived));
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

    // The recordings the archive holds for those days: from backup/{country}/..., and — only when that has none — from the
    // television/... folder the archive used before the backup folder existed.
    private async Task<List<TvRecordingCandidate>> ListArchivedRecordingsAsync(
        DateTimeOffset windowStart, DateTimeOffset windowEnd, List<string> legacyDirectories, string sourceId, string? country,
        TimeSpan offset, CancellationToken cancellationToken)
    {
        var candidates = new List<TvRecordingCandidate>();
        for (var day = DateOnly.FromDateTime(windowStart.DateTime).AddDays(-1); day <= DateOnly.FromDateTime(windowEnd.DateTime); day = day.AddDays(1))
        {
            var directory = ArchiveLayout.BackupDirectory(country, day, sourceId);
            AddCandidates(candidates, directory, await archive!.ListAsync(directory, cancellationToken).ConfigureAwait(false), sourceId, offset, archived: true);
        }

        if (candidates.Count > 0)
        {
            return candidates;
        }

        foreach (var relativeDirectory in legacyDirectories)
        {
            var names = await archive!.ListAsync(relativeDirectory, cancellationToken).ConfigureAwait(false);
            AddCandidates(candidates, relativeDirectory, names, sourceId, offset, archived: true);
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
