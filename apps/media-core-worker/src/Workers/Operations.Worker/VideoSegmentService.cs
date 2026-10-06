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

    public VideoSegmentService(OperationsWorkerOptions options, ICaptureSourceProvider sourceProvider, ILogger<VideoSegmentService> logger)
    {
        this.options = options;
        this.sourceProvider = sourceProvider;
        this.logger = logger;
    }

    public async Task<VideoSegmentResult> GetSegmentAsync(
        string sourceId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var offset = SourceOffset(sourceId);
        var windowStart = startUtc.ToOffset(offset);
        var windowEnd = endUtc.ToOffset(offset);

        // The recording that holds the middle of the window; if that falls in a gap, either end.
        var middle = windowStart + (windowEnd - windowStart) / 2;
        var candidates = ListLocalRecordings(sourceId, offset, windowStart, windowEnd);
        var recording = TvRecordingLocator.Find(candidates, middle)
            ?? TvRecordingLocator.Find(candidates, windowEnd)
            ?? TvRecordingLocator.Find(candidates, windowStart);
        if (recording is null)
        {
            return new VideoSegmentNotAvailable($"No hay video grabado de '{sourceId}' para ese momento.");
        }

        var (startSeconds, endSeconds) = TvRecordingLocator.ClipOffsets(recording, windowStart, windowEnd);
        var inputPath = Path.Combine(options.AudioOutputRootPath, recording.RelativePath);
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

    // The recordings of the days the window touches plus the day before the first: a recording that
    // started in the last minute of a day runs into the next one, and sits in the earlier day's folder.
    private List<TvRecordingCandidate> ListLocalRecordings(string sourceId, TimeSpan offset, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var candidates = new List<TvRecordingCandidate>();
        for (var day = DateOnly.FromDateTime(windowStart.DateTime).AddDays(-1); day <= DateOnly.FromDateTime(windowEnd.DateTime); day = day.AddDays(1))
        {
            var relativeDirectory = string.Join('/', TelevisionDirectory, day.Year.ToString("D4"), day.Month.ToString("D2"), day.Day.ToString("D2"), sourceId);
            var directory = Path.Combine(options.AudioOutputRootPath, relativeDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.mp4"))
            {
                var name = Path.GetFileName(file);
                if (TvRecordingLocator.TryParseStart(name, sourceId, offset, out var start))
                {
                    candidates.Add(new TvRecordingCandidate($"{relativeDirectory}/{name}", start));
                }
            }
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
