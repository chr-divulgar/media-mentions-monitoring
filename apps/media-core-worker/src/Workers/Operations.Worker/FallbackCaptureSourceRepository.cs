using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decorator that reads from Firestore (primary) and only falls back to the local JSON file
/// when Firestore cannot be reached — never both at once. Firestore is the single source of
/// truth for the capture-source catalog; the JSON file exists purely so capture keeps running
/// (on whatever it last knew) during an outage or misconfiguration.
///
/// Intended use: Firestore (primary) + JSON file (secondary) at startup.
/// </summary>
public sealed class FallbackCaptureSourceRepository : ICaptureSourceRepository
{
    private readonly ICaptureSourceRepository primary;
    private readonly ICaptureSourceRepository secondary;
    private readonly ILogger<FallbackCaptureSourceRepository> logger;

    // Which repository is currently authoritative, and which repository each SourceId came from
    // in the last successful list — writes go only there, since exactly one repo is ever "live".
    private volatile bool primaryAvailable = true;
    private volatile IReadOnlyDictionary<string, ICaptureSourceRepository> ownerBySourceId =
        new Dictionary<string, ICaptureSourceRepository>(StringComparer.OrdinalIgnoreCase);

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
        var primaryResult = await TryListAsync(primary, cancellationToken).ConfigureAwait(false);
        if (primaryResult is { } primarySources)
        {
            primaryAvailable = true;
            ownerBySourceId = ToOwnerMap(primarySources, primary);
            return primarySources;
        }

        logger.LogWarning("[FallbackCaptureSourceRepository] Primary repository unreachable; falling back to the local JSON file.");
        primaryAvailable = false;
        var secondarySources = await TryListAsync(secondary, cancellationToken).ConfigureAwait(false) ?? [];
        ownerBySourceId = ToOwnerMap(secondarySources, secondary);
        return secondarySources;
    }

    private static IReadOnlyDictionary<string, ICaptureSourceRepository> ToOwnerMap(
        IReadOnlyList<CaptureSource> sources, ICaptureSourceRepository owner) =>
        sources.ToDictionary(s => s.SourceId, _ => owner, StringComparer.OrdinalIgnoreCase);

    // Null means "unreachable" (an exception, not just an empty catalog) — an empty-but-successful
    // response from the primary is a real answer, not a connection failure, so it is NOT treated
    // as a reason to fall back to the JSON file.
    private async Task<IReadOnlyList<CaptureSource>?> TryListAsync(
        ICaptureSourceRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            return await repository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[FallbackCaptureSourceRepository] Repository ({Type}) failed to list sources.",
                repository.GetType().Name);
            return null;
        }
    }

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
        WriteAsync(repository => repository.UpdateStreamUrlAsync(sourceId, streamUrl, cancellationToken), "UpdateStreamUrlAsync", sourceId);

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
        WriteAsync(repository => repository.UpdateFallbackUrlsAsync(sourceId, fallbackStreamUrls, cancellationToken), "UpdateFallbackUrlsAsync", sourceId);

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
        WriteAsync(repository => repository.UpdateExclusionAsync(sourceId, excluded, cancellationToken), "UpdateExclusionAsync", sourceId);

    private async Task<bool> WriteAsync(
        Func<ICaptureSourceRepository, Task<bool>> operation, string operationName, string sourceId)
    {
        // A sourceId from the last successful list writes back to whichever repo it actually came
        // from; anything else (never listed yet) goes to whichever repo is currently authoritative.
        if (!ownerBySourceId.TryGetValue(sourceId, out var target))
        {
            target = primaryAvailable ? primary : secondary;
        }

        try
        {
            return await operation(target).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[FallbackCaptureSourceRepository] Repository ({Type}) failed during {Operation} for {SourceId}.",
                target.GetType().Name,
                operationName,
                sourceId);
            return false;
        }
    }
}
