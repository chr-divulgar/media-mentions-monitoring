using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decorator that tries the primary repository first and, on any failure or empty result,
/// falls back to the secondary repository with a structured warning log.
/// Intended use: Firebase Realtime Database (primary) + JSON file (secondary) at startup.
/// </summary>
public sealed class FallbackCaptureSourceRepository : ICaptureSourceRepository
{
    private readonly ICaptureSourceRepository primary;
    private readonly ICaptureSourceRepository secondary;
    private readonly ILogger<FallbackCaptureSourceRepository> logger;

    public FallbackCaptureSourceRepository(
        ICaptureSourceRepository primary,
        ICaptureSourceRepository secondary,
        ILogger<FallbackCaptureSourceRepository> logger)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        this.secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sources = await primary.ListAllAsync(cancellationToken).ConfigureAwait(false);

            if (sources.Count == 0)
            {
                logger.LogWarning(
                    "[FallbackCaptureSourceRepository] Primary repository ({PrimaryType}) returned zero sources. " +
                    "Falling back to {SecondaryType}.",
                    primary.GetType().Name, secondary.GetType().Name);
                return await secondary.ListAllAsync(cancellationToken).ConfigureAwait(false);
            }

            return sources;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled the operation — do not fall back, propagate cleanly.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[FallbackCaptureSourceRepository] Primary repository ({PrimaryType}) failed. " +
                "Falling back to {SecondaryType}.",
                primary.GetType().Name, secondary.GetType().Name);

            return await secondary.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
