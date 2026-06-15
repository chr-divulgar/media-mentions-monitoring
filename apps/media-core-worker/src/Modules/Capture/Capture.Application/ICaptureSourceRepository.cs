using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Outbound port for reading the configured capture source catalog.
/// Implementations may be backed by a remote database, a local file, or a composed chain.
/// Returns all sources including excluded ones — filtering is the caller's responsibility.
/// </summary>
public interface ICaptureSourceRepository
{
    Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default);
}
