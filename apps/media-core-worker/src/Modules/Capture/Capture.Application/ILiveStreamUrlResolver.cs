using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public interface ILiveStreamUrlResolver
{
    bool CanResolve(CaptureSource source);

    Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(
        CaptureSource source,
        CancellationToken cancellationToken = default);
}
