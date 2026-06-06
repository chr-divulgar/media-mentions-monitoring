using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public interface IStartupSourceDiscoveryService
{
    Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default);
}
