using System.Collections.Concurrent;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Modules.Capture.Application;

public sealed class ContinuousCaptureUseCase : IContinuousCaptureUseCase
{
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly IAudioCapturePlugin audioCapturePlugin;
    private readonly int maxDegreeOfParallelism;

    public ContinuousCaptureUseCase(
        ICaptureSourceProvider captureSourceProvider,
        IIngestionPluginResolver pluginResolver,
        IAudioCapturePlugin audioCapturePlugin,
        int maxDegreeOfParallelism)
    {
        this.captureSourceProvider = captureSourceProvider;
        this.pluginResolver = pluginResolver;
        this.audioCapturePlugin = audioCapturePlugin;
        this.maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
    }

    public async Task<ContinuousCaptureResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);

        var attempts = 0;
        var succeeded = 0;
        var failed = 0;

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
                try
                {
                    var plan = await pluginResolver
                        .ResolveAsync(source, IngestionMode.Continuous, ct)
                        .ConfigureAwait(false);

                    var result = await audioCapturePlugin
                        .CaptureAsync(source, plan, ct)
                        .ConfigureAwait(false);

                    if (result.Succeeded)
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.StartsWith("No plugin profile configured", StringComparison.Ordinal))
                {
                    return;
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
            }).ConfigureAwait(false);

        return new ContinuousCaptureResult(attempts, succeeded, failed, null);
    }
}
