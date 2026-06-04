using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public interface ICaptureSourceProvider
{
    Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default);
}