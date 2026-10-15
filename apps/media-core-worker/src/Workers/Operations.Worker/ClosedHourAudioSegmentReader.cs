using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Resolves a sourceId + UTC window into the on-disk hourly .opus file(s) that cover it (the same
/// naming convention CaptureSession.CurrentOpusPath/ResolveSourceDirectory write to — see
/// InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName/AlignWindow, reused here rather than
/// duplicated), and hands off to OpusSegmentTranscoder for the actual decode/cut/encode work.
///
/// An hour that is no longer on this disk (the purge keeps only today's) is looked up in the archive, where
/// it is kept as a small opus (or, for hours archived before that format, an mp3): ArchiveDownloadCache fetches
/// it once and it is then cut like any other source.
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
    private readonly IArchiveStorage? archive;
    private readonly ArchiveDownloadCache? cache;

    public ClosedHourAudioSegmentReader(
        ICaptureSourceRepository captureSourceRepository,
        IIngestionPluginResolver pluginResolver,
        ILiveCaptureProgressReader liveCaptureProgressReader,
        OperationsWorkerOptions options,
        ILogger<ClosedHourAudioSegmentReader> logger,
        IArchiveStorage? archive = null)
    {
        this.archive = archive;
        cache = archive is null
            ? null
            : new ArchiveDownloadCache(
                archive,
                Path.Combine(options.StageFilesystemRootPath, "audio-cache"),
                TimeSpan.FromHours(options.AudioCacheHours),
                options.AudioCacheMaxGb * 1024L * 1024 * 1024,
                logger);
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

        var resolved = await ResolveOrderedFilesAsync(
            sourceId, source.Country, mediaDirectory, firstHourStart, endLocal, currentHourStart, rotationInterval, liveProgress, cancellationToken)
            .ConfigureAwait(false);
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

    // Walks the window hour by hour, collecting the closed-hour files that exist (on this disk, else the
    // archived mp3) and — for the live hour, if the window reaches it — a point-in-time copy of the file
    // the session still has open (see TrySnapshotLiveFile). Stops at the first hour that turns out to be
    // unavailable, returning whatever was collected before it: a gap partway through a window is served
    // as far as it goes rather than rejected outright.
    private async Task<ResolvedFiles> ResolveOrderedFilesAsync(
        string sourceId,
        string? country,
        string mediaDirectory,
        DateTimeOffset firstHourStart,
        DateTimeOffset endLocal,
        DateTimeOffset currentHourStart,
        TimeSpan rotationInterval,
        LiveCaptureProgress? liveProgress,
        CancellationToken cancellationToken)
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
                var archived = await FindArchivedHourAsync(mediaDirectory, sourceId, country, hourStart, cancellationToken).ConfigureAwait(false);
                if (archived.Path is not null)
                {
                    files.Add(archived.Path);
                    continue;
                }

                return files.Count == 0
                    ? new ResolvedFiles(files, null, new ClosedHourAudioNotAvailable(
                        archived.Reason ?? $"No recording found for '{sourceId}' at {hourStart:yyyy-MM-dd HH:mm}."))
                    : new ResolvedFiles(files, null, null); // a gap mid-window — serve what came before it
            }

            files.Add(candidate);
        }

        return new ResolvedFiles(files, null, null);
    }

    // The hour as the archive holds it, fetched into the cache on first use: the opus that replaced the local one,
    // else the mp3 that radio was archived as at first.
    // Without an archive this is "nothing" and the caller reports the plain "no recording" it always did. With
    // one, a missing hour and an archive that cannot be reached get different messages, because only the
    // second is worth retrying.
    private async Task<(string? Path, string? Reason)> FindArchivedHourAsync(
        string mediaDirectory, string sourceId, string? country, DateTimeOffset hourStart, CancellationToken cancellationToken)
    {
        if (archive is null || cache is null)
        {
            return (null, null);
        }

        // backup/{country}/{yyyy}/{MM}/{dd}/{source} is where the hour is archived now; {medium}/{yyyy}/... is where it was
        // before the backup folder existed.
        string[] directories =
        [
            ArchiveLayout.BackupDirectory(country, DateOnly.FromDateTime(hourStart.DateTime), sourceId),
            $"{mediaDirectory}/{hourStart:yyyy}/{hourStart:MM}/{hourStart:dd}/{sourceId}",
        ];
        var baseName = $"{sourceId}_{hourStart:yyyy-MM-dd_HH-mm-ss}";
        string[] candidates = [$"{baseName}.opus", $"{baseName}.mp3"];

        foreach (var directoryOption in directories)
        {
            foreach (var candidate in candidates)
            {
                if (cache.IsFresh($"{directoryOption}/{candidate}"))
                {
                    return (cache.PathFor($"{directoryOption}/{candidate}"), null);
                }
            }
        }

        string? name = null;
        var directory = directories[0];
        try
        {
            foreach (var directoryOption in directories)
            {
                var names = await archive.ListAsync(directoryOption, cancellationToken).ConfigureAwait(false);
                name = candidates.FirstOrDefault(candidate => names.Contains(candidate, StringComparer.Ordinal));
                if (name is not null)
                {
                    directory = directoryOption;
                    break;
                }
            }

            if (name is null)
            {
                return (null, $"No hay audio archivado de '{sourceId}' para {hourStart:yyyy-MM-dd HH:mm}.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[ClosedHourAudioSegmentReader] Could not list the archive for {SourceId}.", sourceId);
            return (null, "No se pudo consultar el archivo de audio. Intente de nuevo en unos minutos.");
        }

        var relativePath = $"{directory}/{name}";
        return await cache.EnsureAsync(relativePath, cancellationToken).ConfigureAwait(false)
            ? (cache.PathFor(relativePath), null)
            : (null, "No se pudo descargar el audio del archivo. Intente de nuevo en unos minutos.");
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

            // After a mid-hour session restart the audio keeps going into "<hour>.opus.resume"
            // and is only merged at rotation; without it the snapshot ends where the restart
            // happened and later alerts fall outside the clip.
            var resumePath = liveOpusPath + ".resume";
            if (File.Exists(resumePath))
            {
                var resumeCopy = snapshotPath + ".resume";
                var joinedPath = snapshotPath + ".joined.opus";
                try
                {
                    File.Copy(resumePath, resumeCopy, overwrite: true);
                    OpusFileJoiner.Join([snapshotPath, resumeCopy], joinedPath);
                    File.Move(joinedPath, snapshotPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex, "[ClosedHourAudioSegmentReader] Could not merge the resume tail for source {SourceId}; serving the main file only.",
                        sourceId);
                }
                finally
                {
                    File.Delete(resumeCopy);
                    File.Delete(joinedPath);
                }
            }

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
