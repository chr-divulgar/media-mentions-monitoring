using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public sealed class ContinuousCaptureUseCase : IContinuousCaptureUseCase
{
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly IAudioCapturePlugin audioCapturePlugin;
    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ICaptureAttemptObserver captureAttemptObserver;
    private readonly int maxDegreeOfParallelism;

    public ContinuousCaptureUseCase(
        ICaptureSourceProvider captureSourceProvider,
        IIngestionPluginResolver pluginResolver,
        IAudioCapturePlugin audioCapturePlugin,
        IMonitoringArtifactRepository monitoringArtifactRepository,
        int maxDegreeOfParallelism,
        ICaptureAttemptObserver? captureAttemptObserver = null)
    {
        this.captureSourceProvider = captureSourceProvider;
        this.pluginResolver = pluginResolver;
        this.audioCapturePlugin = audioCapturePlugin;
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        this.captureAttemptObserver = captureAttemptObserver ?? NullCaptureAttemptObserver.Instance;
    }

    public async Task<ContinuousCaptureResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);

        var attempts = 0;
        var succeeded = 0;
        var failed = 0;
        var capturedAtValues = new ConcurrentBag<DateTimeOffset>();

        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (source, ct) =>
            {
                Interlocked.Increment(ref attempts);
                var capturedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(source.UtcOffsetMinutes));

                try
                {
                    var plan = await pluginResolver
                        .ResolveAsync(source, IngestionMode.Continuous, ct)
                        .ConfigureAwait(false);

                    var captureResult = await audioCapturePlugin
                        .CaptureAsync(source, plan, ct)
                        .ConfigureAwait(false);

                    var artifact = BuildArtifact(source, capturedAt, captureResult);
                    await monitoringArtifactRepository.UpsertAsync(artifact, ct).ConfigureAwait(false);

                    if (captureResult.Succeeded)
                    {
                        Interlocked.Increment(ref succeeded);
                        capturedAtValues.Add(capturedAt);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }

                    await captureAttemptObserver
                        .ReportAsync(source, captureResult, ct)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.StartsWith("No plugin profile configured", StringComparison.Ordinal))
                {
                    return;
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                    await captureAttemptObserver
                        .ReportAsync(source, new AudioCaptureExecutionResult(false, string.Empty, "capture execution failed"), ct)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        DateTimeOffset? lastCapturedAtUtc = capturedAtValues.Count == 0
            ? (DateTimeOffset?)null
            : capturedAtValues.Max();

        return new ContinuousCaptureResult(attempts, succeeded, failed, lastCapturedAtUtc);
    }

    private static MonitoringArtifact BuildArtifact(
        MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
        DateTimeOffset capturedAtUtc,
        AudioCaptureExecutionResult captureResult)
    {
        var payload = JsonSerializer.Serialize(new
        {
            source.Platform,
            source.Media,
            source.StreamUrl,
            captureResult.Succeeded,
            captureResult.OpusFilePath,
            captureResult.SilenceFilledSeconds,
            captureResult.ErrorMessage
        });

        return new MonitoringArtifact(
            id: $"capture-{source.SourceId}-{capturedAtUtc:yyyyMMddHHmmssfff}",
            tenantId: source.TenantId,
            source: source.SourceId,
            kind: "capture",
            payloadJson: payload,
            capturedAtUtc: capturedAtUtc);
    }
}

