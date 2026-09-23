using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Resolves a sourceId + UTC window into the on-disk hourly .opus file(s) that cover it (the same
/// naming convention CaptureSession.CurrentOpusPath/ResolveSourceDirectory write to — see
/// InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName/AlignWindow, reused here rather than
/// duplicated), and hands off to OpusSegmentTranscoder for the actual decode/cut/encode work.
///
/// A window that touches the still-recording current hour is served from a point-in-time copy of
/// that hour's file rather than the live one — see TrySnapshotLiveFile — so extraction reads
/// whatever the session has flushed so far without ever contending with FFmpeg's own writes to it.
/// </summary>
public sealed class ClosedHourAudioSegmentReader : IClosedHourAudioReader
{
    private readonly ICaptureSourceRepository captureSourceRepository;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly ILiveCaptureProgressReader liveCaptureProgressReader;
    private readonly OperationsWorkerOptions options;
    private readonly ILogger<ClosedHourAudioSegmentReader> logger;

    public ClosedHourAudioSegmentReader(
        ICaptureSourceRepository captureSourceRepository,
        IIngestionPluginResolver pluginResolver,
        ILiveCaptureProgressReader liveCaptureProgressReader,
        OperationsWorkerOptions options,
        ILogger<ClosedHourAudioSegmentReader> logger)
    {
        this.captureSourceRepository = captureSourceRepository;
        this.pluginResolver = pluginResolver;
        this.liveCaptureProgressReader = liveCaptureProgressReader;
        this.options = options;
        this.logger = logger;
    }

    public async Task<ClosedHourAudioResult> ExtractSegmentAsync(
        string sourceId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bitrateKbps,
        int frequencyHz,
        string format,
        CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var source = sources.FirstOrDefault(
            s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return new ClosedHourAudioSourceNotFound();
        }

        var plan = await pluginResolver.ResolveAsync(source, IngestionMode.Continuous, cancellationToken)
            .ConfigureAwait(false);
        var rotationInterval = plan.OpusRotationInterval > TimeSpan.Zero
            ? plan.OpusRotationInterval
            : TimeSpan.FromHours(1);

        var sourceOffset = TimeSpan.FromMinutes(source.UtcOffsetMinutes);
        var currentHourStart = InProcessFfmpegAudioCapturePlugin.AlignWindow(
            DateTimeOffset.UtcNow.ToOffset(sourceOffset), rotationInterval);

        var touchesLiveHour = endUtc >= currentHourStart;
        var liveProgress = touchesLiveHour ? liveCaptureProgressReader.TryGetLiveProgress(sourceId) : null;
        if (touchesLiveHour && liveProgress is null)
        {
            // Nothing is capturing this source right now — no live file to snapshot from.
            return new ClosedHourAudioStillRecording(0);
        }

        var mediaDirectory = InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName(source.Media);
        var startLocal = startUtc.ToOffset(sourceOffset);
        var endLocal = endUtc.ToOffset(sourceOffset);
        var firstHourStart = InProcessFfmpegAudioCapturePlugin.AlignWindow(startLocal, rotationInterval);

        var resolved = ResolveOrderedFiles(
            sourceId, mediaDirectory, firstHourStart, endLocal, currentHourStart, rotationInterval, liveProgress);
        if (resolved.Rejection is not null)
        {
            return resolved.Rejection;
        }

        try
        {
            var windowStartOffsetSeconds = (startLocal - firstHourStart).TotalSeconds;
            var durationSeconds = (endUtc - startUtc).TotalSeconds;

            var bytes = await Task.Run(
                () => OpusSegmentTranscoder.Transcode(
                    resolved.Files, windowStartOffsetSeconds, durationSeconds, frequencyHz, bitrateKbps, format),
                cancellationToken).ConfigureAwait(false);

            var contentType = string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase)
                ? "audio/wav"
                : "audio/mpeg";
            return new ClosedHourAudioSuccess(bytes, contentType);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception, "[ClosedHourAudioSegmentReader] Failed to extract segment for source {SourceId}.", sourceId);
            return new ClosedHourAudioNotAvailable($"Could not extract audio for '{sourceId}': {exception.Message}");
        }
        finally
        {
            if (resolved.LiveSnapshotPath is not null)
            {
                try { File.Delete(resolved.LiveSnapshotPath); }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex, "[ClosedHourAudioSegmentReader] Could not delete live snapshot {Path}.", resolved.LiveSnapshotPath);
                }
            }
        }
    }

    private readonly record struct ResolvedFiles(
        IReadOnlyList<string> Files, string? LiveSnapshotPath, ClosedHourAudioResult? Rejection);

    // Walks the window hour by hour, collecting the closed-hour files that exist and — for the
    // live hour, if the window reaches it — a point-in-time copy of the file the session still has
    // open (see TrySnapshotLiveFile). Stops at the first hour that turns out to be unavailable,
    // returning whatever was collected before it: a gap partway through a window is served as far
    // as it goes rather than rejected outright.
    private ResolvedFiles ResolveOrderedFiles(
        string sourceId,
        string mediaDirectory,
        DateTimeOffset firstHourStart,
        DateTimeOffset endLocal,
        DateTimeOffset currentHourStart,
        TimeSpan rotationInterval,
        LiveCaptureProgress? liveProgress)
    {
        var files = new List<string>();

        for (var hourStart = firstHourStart; hourStart < endLocal; hourStart += rotationInterval)
        {
            var candidate = BuildOpusPath(options.AudioOutputRootPath, mediaDirectory, sourceId, hourStart);

            if (hourStart >= currentHourStart)
            {
                var liveSnapshotPath = TrySnapshotLiveFile(candidate, sourceId);
                if (liveSnapshotPath is null)
                {
                    return files.Count == 0
                        ? new ResolvedFiles(files, null, new ClosedHourAudioStillRecording(liveProgress?.RecordedSeconds ?? 0))
                        : new ResolvedFiles(files, null, null);
                }

                files.Add(liveSnapshotPath);
                return new ResolvedFiles(files, liveSnapshotPath, null); // nothing exists past the live hour
            }

            if (!File.Exists(candidate))
            {
                return files.Count == 0
                    ? new ResolvedFiles(files, null, new ClosedHourAudioNotAvailable(
                        $"No recording found for '{sourceId}' at {hourStart:yyyy-MM-dd HH:mm}."))
                    : new ResolvedFiles(files, null, null); // a gap mid-window — serve what came before it

            }

            files.Add(candidate);
        }

        return new ResolvedFiles(files, null, null);
    }

    // Copies the live hour's file to a temp path before reading it, so the actual extraction
    // (which holds the file open far longer than a quick probe) never contends with the capture
    // session's own writes. A plain File.Copy while the source is open for write is exactly what
    // this needs — Windows' default file share mode permits concurrent reads, and FFmpeg's own
    // file writer doesn't request an exclusive lock (the worker already relies on that elsewhere,
    // e.g. probing a file mid-resume). Returns null if the file doesn't exist yet (session started
    // seconds ago, before its first flush) or the copy fails for any reason — the caller falls
    // back to reporting "still recording" rather than serving a snapshot it isn't sure of.
    private string? TrySnapshotLiveFile(string liveOpusPath, string sourceId)
    {
        if (!File.Exists(liveOpusPath))
        {
            return null;
        }

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"live-audio-snapshot-{Guid.NewGuid():N}.opus");
        try
        {
            File.Copy(liveOpusPath, snapshotPath, overwrite: true);
            return snapshotPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "[ClosedHourAudioSegmentReader] Could not snapshot the live file for source {SourceId} at {Path}.",
                sourceId, liveOpusPath);
            return null;
        }
    }

    private static string BuildOpusPath(
        string audioOutputRootPath, string mediaDirectory, string sourceId, DateTimeOffset hourStart) =>
        Path.Combine(
            audioOutputRootPath,
            mediaDirectory,
            hourStart.ToString("yyyy"),
            hourStart.ToString("MM"),
            hourStart.ToString("dd"),
            sourceId,
            $"{sourceId}_{hourStart:yyyy-MM-dd_HH-mm-ss}.opus");
}
