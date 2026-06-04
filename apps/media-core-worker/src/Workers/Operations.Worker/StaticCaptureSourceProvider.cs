using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StaticCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly CaptureSource source;

    public StaticCaptureSourceProvider(OperationsWorkerOptions options)
    {
        source = new CaptureSource(
            options.CaptureSourceId,
            options.TenantId,
            options.CapturePlatform,
            options.CaptureMedia,
            options.CaptureStreamUrl);
    }

    public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CaptureSource>>(new[] { source });
    }
}