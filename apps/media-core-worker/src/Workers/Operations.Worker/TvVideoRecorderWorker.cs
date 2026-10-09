using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Records every TV source as hourly MP4 files (RQ-011), next to the .opus the audio capture keeps
/// writing. Deliberately separate from InProcessFfmpegAudioCapturePlugin: transcription and alerts
/// stay on the proven audio path, and a video failure can never take audio down with it.
///
/// Each hour is a fresh in-process stream copy (InProcessMp4Recorder, on the FFmpeg libraries the
/// worker already ships — nothing installed on the host) on a freshly resolved URL: YouTube live
/// URLs expire after a few hours, so re-resolving per file is also what keeps it alive.
/// </summary>
public sealed class TvVideoRecorderWorker : BackgroundService
{
    private const string TelevisionDirectory = "television";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    // How often the set of active TV sources is re-read: a channel excluded at startup and later
    // recovered by the reconciliation (or newly added) starts recording within a minute.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);

    private readonly OperationsWorkerOptions options;
    private readonly ICaptureSourceProvider sourceProvider;
    private readonly Func<IReadOnlyList<string>, TimeSpan, string, CancellationToken, Task<string?>> record;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<TvVideoRecorderWorker> logger;
    private readonly Func<CaptureSource, CancellationToken, Task<IReadOnlyList<string>>> resolveVideoUrl;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, Task> recorders = new(StringComparer.OrdinalIgnoreCase);

    public TvVideoRecorderWorker(
        OperationsWorkerOptions options,
        ICaptureSourceProvider sourceProvider,
        YtdlpLiveStreamUrlResolver resolver,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<TvVideoRecorderWorker> logger)
        : this(
            options, sourceProvider, incidentRecorder, logger,
            (source, ct) => ResolveAsync(resolver, options, source, ct),
            (urls, duration, path, ct) => Task.Run(() => InProcessMp4Recorder.Record(urls, duration, path, ct), ct))
    {
    }

    // Test seam: URL resolution and the recording itself are injected, so a pass runs without
    // yt-dlp or a live stream. The recorder returns null when the hour completed, else why not.
    internal TvVideoRecorderWorker(
        OperationsWorkerOptions options,
        ICaptureSourceProvider sourceProvider,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<TvVideoRecorderWorker> logger,
        Func<CaptureSource, CancellationToken, Task<IReadOnlyList<string>>> resolveVideoUrl,
        Func<IReadOnlyList<string>, TimeSpan, string, CancellationToken, Task<string?>> record,
        Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.options = options;
        this.sourceProvider = sourceProvider;
        this.record = record;
        this.incidentRecorder = incidentRecorder;
        this.logger = logger;
        this.resolveVideoUrl = resolveVideoUrl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "[TvVideoRecorderWorker] Could not refresh the TV sources.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(recorders.Values).ConfigureAwait(false);
    }

    // Starts a recorder for every active TV source that has none running. The source list is the
    // capture's own in-memory set (ICaptureSourceProvider): excluded sources are not in it, and the
    // reconciliation puts them back once they recover — so this follows exactly what audio does,
    // without reading the database every minute. Returns the recorders it started.
    internal async Task<IReadOnlyList<Task>> ReconcileAsync(CancellationToken cancellationToken)
    {
        var started = new List<Task>();
        foreach (var source in await ActiveTvSourcesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (recorders.TryGetValue(source.SourceId, out var running) && !running.IsCompleted)
            {
                continue;
            }

            logger.LogInformation("[TvVideoRecorderWorker] Starting MP4 recording for {SourceId}.", source.SourceId);
            var recorder = RecordLoopAsync(source.SourceId, cancellationToken);
            recorders[source.SourceId] = recorder;
            started.Add(recorder);
        }

        return started;
    }

    private async Task<IReadOnlyList<CaptureSource>> ActiveTvSourcesAsync(CancellationToken cancellationToken) =>
        (await sourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false))
            .Where(source => InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName(source.Media) == TelevisionDirectory)
            .ToArray();

    // Records hour after hour while the source stays active; stops as soon as it is excluded (the
    // next ReconcileAsync restarts it if it comes back). Re-reads the source each hour so a changed
    // URL or offset is picked up.
    private async Task RecordLoopAsync(string sourceId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool ok;
            try
            {
                var source = (await ActiveTvSourcesAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
                if (source is null)
                {
                    logger.LogInformation("[TvVideoRecorderWorker] {SourceId} is no longer active; MP4 recording paused.", sourceId);
                    return;
                }

                ok = await RecordOnceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[TvVideoRecorderWorker] Recording pass failed for {SourceId}.", sourceId);
                ok = false;
            }

            if (!ok)
            {
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // One file: resolve, plan, record until the hour turns. True when it ran to its planned end.
    internal async Task<bool> RecordOnceAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        var urls = await resolveVideoUrl(source, cancellationToken).ConfigureAwait(false);
        if (urls.Count == 0)
        {
            incidentRecorder.Record(source.SourceId, IncidentKinds.Failed, "Video MP4: no se pudo obtener la URL del video");
            return false;
        }

        var startedAt = clock().ToOffset(TimeSpan.FromMinutes(source.UtcOffsetMinutes));
        var plan = TvVideoRecording.Plan(options.AudioOutputRootPath, source.Media, source.SourceId, startedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);

        string? stopReason;
        try
        {
            stopReason = await record(urls, plan.Duration, plan.OutputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopReason = exception.Message;
        }

        if (stopReason is null)
        {
            return true;
        }

        incidentRecorder.Record(source.SourceId, IncidentKinds.Failed, $"Video MP4: {stopReason}");
        logger.LogWarning("[TvVideoRecorderWorker] {SourceId} video stopped after {Seconds:0}s: {Reason}",
            source.SourceId, (clock() - startedAt).TotalSeconds, stopReason);
        return false;
    }

    private static async Task<IReadOnlyList<string>> ResolveAsync(
        YtdlpLiveStreamUrlResolver resolver, OperationsWorkerOptions options, CaptureSource source, CancellationToken cancellationToken) =>
        resolver.CanResolve(source)
            ? await resolver.TryResolveVideoUrlsAsync(source, options.TvVideoMaxHeight, cancellationToken).ConfigureAwait(false)
            : string.IsNullOrWhiteSpace(source.StreamUrl) ? [] : [source.StreamUrl];

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
