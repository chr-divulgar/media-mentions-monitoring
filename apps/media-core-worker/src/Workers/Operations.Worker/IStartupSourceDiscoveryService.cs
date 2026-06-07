using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public interface IStartupSourceDiscoveryService
{
    Task<IReadOnlyList<string>> DiscoverStreamUrlsAsync(CaptureSource source, CancellationToken cancellationToken = default);

    Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default);
}
