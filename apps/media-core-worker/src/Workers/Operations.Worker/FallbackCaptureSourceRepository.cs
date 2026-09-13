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

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
    {
        return UpdateWithFallbackAsync(
            primaryOperation: repository => repository.UpdateStreamUrlAsync(sourceId, streamUrl, cancellationToken),
            secondaryOperation: repository => repository.UpdateStreamUrlAsync(sourceId, streamUrl, cancellationToken),
            operationName: "UpdateStreamUrlAsync",
            sourceId: sourceId,
            cancellationToken: cancellationToken);
    }

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
    {
        return UpdateWithFallbackAsync(
            primaryOperation: repository => repository.UpdateFallbackUrlsAsync(sourceId, fallbackStreamUrls, cancellationToken),
            secondaryOperation: repository => repository.UpdateFallbackUrlsAsync(sourceId, fallbackStreamUrls, cancellationToken),
            operationName: "UpdateFallbackUrlsAsync",
            sourceId: sourceId,
            cancellationToken: cancellationToken);
    }

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
    {
        return UpdateWithFallbackAsync(
            primaryOperation: repository => repository.UpdateExclusionAsync(sourceId, excluded, cancellationToken),
            secondaryOperation: repository => repository.UpdateExclusionAsync(sourceId, excluded, cancellationToken),
            operationName: "UpdateExclusionAsync",
            sourceId: sourceId,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> UpdateWithFallbackAsync(
        Func<ICaptureSourceRepository, Task<bool>> primaryOperation,
        Func<ICaptureSourceRepository, Task<bool>> secondaryOperation,
        string operationName,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var primarySucceeded = false;
        var secondarySucceeded = false;

        try
        {
            primarySucceeded = await primaryOperation(primary).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[FallbackCaptureSourceRepository] Primary repository ({PrimaryType}) failed during {Operation} for {SourceId}.",
                primary.GetType().Name,
                operationName,
                sourceId);
        }

        try
        {
            secondarySucceeded = await secondaryOperation(secondary).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[FallbackCaptureSourceRepository] Secondary repository ({SecondaryType}) failed during {Operation} for {SourceId}.",
                secondary.GetType().Name,
                operationName,
                sourceId);
        }

        if (!primarySucceeded)
        {
            logger.LogWarning(
                "[FallbackCaptureSourceRepository] Primary write did not apply during {Operation} for {SourceId}. SecondaryApplied={SecondaryApplied}.",
                operationName,
                sourceId,
                secondarySucceeded);
        }

        return primarySucceeded || secondarySucceeded;
    }
}
