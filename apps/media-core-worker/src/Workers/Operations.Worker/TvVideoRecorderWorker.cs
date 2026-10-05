using MediaOpsCore.BuildingBlocks.Application;
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
/// Each hour is a fresh ffmpeg.exe stream copy (no re-encoding) on a freshly resolved URL — YouTube
/// live URLs expire after a few hours, so re-resolving per file is also what keeps it alive.
/// </summary>
public sealed class TvVideoRecorderWorker : BackgroundService
{
    private const string TelevisionDirectory = "television";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    // ffmpeg stops itself at -t; the process timeout only catches a hung process.
    private static readonly TimeSpan HangMargin = TimeSpan.FromMinutes(2);
    // A file that ended this much earlier than planned means the stream dropped, not that it finished.
    private static readonly TimeSpan EarlyEndTolerance = TimeSpan.FromSeconds(30);

    private readonly OperationsWorkerOptions options;
    private readonly IProcessRunner processRunner;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly ILogger<TvVideoRecorderWorker> logger;
    private readonly Func<CaptureSource, CancellationToken, Task<IReadOnlyList<string>>> resolveVideoUrl;
    private readonly ICaptureSourceRepository? sourceRepository;
    private readonly Func<DateTimeOffset> clock;

    public TvVideoRecorderWorker(
        OperationsWorkerOptions options,
        ICaptureSourceRepository sourceRepository,
        YtdlpLiveStreamUrlResolver resolver,
        IProcessRunner processRunner,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<TvVideoRecorderWorker> logger)
        : this(options, processRunner, incidentRecorder, logger, (source, ct) => ResolveAsync(resolver, options, source, ct))
    {
        this.sourceRepository = sourceRepository;
    }

    // Test seam: the URL resolution is injected, so one recording pass runs without yt-dlp.
    internal TvVideoRecorderWorker(
        OperationsWorkerOptions options,
        IProcessRunner processRunner,
        ISourceIncidentRecorder incidentRecorder,
        ILogger<TvVideoRecorderWorker> logger,
        Func<CaptureSource, CancellationToken, Task<IReadOnlyList<string>>> resolveVideoUrl,
        Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.options = options;
        this.processRunner = processRunner;
        this.incidentRecorder = incidentRecorder;
        this.logger = logger;
        this.resolveVideoUrl = resolveVideoUrl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sourceRepository is null)
        {
            return;
        }

        // ponytail: TV sources are read once at startup (a restart picks up a new channel), same as
        // capture itself; re-read periodically if channels start being added without restarts.
        var sources = (await sourceRepository.ListAllAsync(stoppingToken).ConfigureAwait(false))
            .Where(source => !source.IsExcluded
                && InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName(source.Media) == TelevisionDirectory)
            .ToArray();

        logger.LogInformation("[TvVideoRecorderWorker] Recording MP4 for {Count} TV source(s).", sources.Length);
        await Task.WhenAll(sources.Select(source => RecordLoopAsync(source, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RecordLoopAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool ok;
            try
            {
                ok = await RecordOnceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[TvVideoRecorderWorker] Recording pass failed for {SourceId}.", source.SourceId);
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

        var result = await processRunner.RunAsync(
            new ProcessCommand(
                options.FfmpegExecutablePath,
                TvVideoRecording.FfmpegArguments(urls, plan.Duration, plan.OutputPath),
                timeout: plan.Duration + HangMargin),
            cancellationToken).ConfigureAwait(false);

        var ranFor = clock() - startedAt;
        if (result.TimedOut || (result.ExitCode == 0 && ranFor >= plan.Duration - EarlyEndTolerance))
        {
            return true;
        }

        var reason = FirstLine(result.StandardError) ?? $"ffmpeg terminó con código {result.ExitCode}";
        incidentRecorder.Record(source.SourceId, IncidentKinds.Failed, $"Video MP4: {reason}");
        logger.LogWarning("[TvVideoRecorderWorker] {SourceId} video stopped after {Seconds:0}s: {Reason}", source.SourceId, ranFor.TotalSeconds, reason);
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
