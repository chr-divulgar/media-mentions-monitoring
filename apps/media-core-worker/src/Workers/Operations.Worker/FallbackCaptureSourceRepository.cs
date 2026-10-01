using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decorator that unions the primary and secondary repositories' sources by SourceId (primary
/// wins on a collision), instead of an all-or-nothing choice between them.
///
/// The all-or-nothing version of this class — use primary entirely if it returns anything at
/// all, only fall back to secondary on failure/empty — silently discarded every source that
/// only lived in the JSON file the moment Firestore's "platforms" collection had any data at
/// all, even though the two catalogs cover almost entirely different, non-overlapping stations
/// (Firestore holds the newer sources ops enters through the Plataformas UI; the JSON file holds
/// the older ones the worker has been self-healing streamUrls for over months). That is not a
/// real fallback relationship — both are genuinely partial catalogs today, and neither can safely
/// stand in for the other going missing.
///
/// Intended use: Firestore (primary) + JSON file (secondary) at startup.
/// </summary>
public sealed class FallbackCaptureSourceRepository : ICaptureSourceRepository
{
    private readonly ICaptureSourceRepository primary;
    private readonly ICaptureSourceRepository secondary;
    private readonly ILogger<FallbackCaptureSourceRepository> logger;

    // Which repository each SourceId came from in the last merge. Writes go only to the owner:
    // writing a JSON-only station to Firestore cost a runQuery per attempt (and a "No document
    // found" warning) on every hot-recovery cycle, which was a real share of the 429s.
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
        var primarySources = await TryListAsync(primary, "Primary", cancellationToken).ConfigureAwait(false);
        var secondarySources = await TryListAsync(secondary, "Secondary", cancellationToken).ConfigureAwait(false);

        var merged = new Dictionary<string, CaptureSource>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, ICaptureSourceRepository>(StringComparer.OrdinalIgnoreCase);
        var primaryStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in primarySources)
        {
            merged[source.SourceId] = source;
            owners[source.SourceId] = primary;
            primaryStreams.Add(StreamIdentity(source.StreamUrl));
        }

        foreach (var source in secondarySources)
        {
            // The same station often exists in both catalogs under a different SourceId (e.g.
            // "colmundo-radio-bogota" in the JSON file, "Colmundo" in Firestore). Keeping both
            // recorded it twice, alerted twice, and some servers cut the duplicate connection
            // every few seconds. Primary wins, same as on a SourceId collision.
            if (primaryStreams.Contains(StreamIdentity(source.StreamUrl)))
            {
                continue;
            }

            if (merged.TryAdd(source.SourceId, source))
            {
                owners[source.SourceId] = secondary;
            }
        }

        ownerBySourceId = owners;
        return merged.Values.ToArray();
    }

    // http/https and a trailing slash reach the same stream.
    private static string StreamIdentity(string streamUrl) =>
        Uri.TryCreate(streamUrl.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Host}{uri.PathAndQuery.TrimEnd('/')}"
            : streamUrl.Trim().TrimEnd('/');

    private async Task<IReadOnlyList<CaptureSource>> TryListAsync(
        ICaptureSourceRepository repository, string role, CancellationToken cancellationToken)
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
                "[FallbackCaptureSourceRepository] {Role} repository ({Type}) failed to list sources; treating it as empty for this merge.",
                role,
                repository.GetType().Name);
            return [];
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
        if (ownerBySourceId.TryGetValue(sourceId, out var owner))
        {
            var operation = ReferenceEquals(owner, primary) ? primaryOperation : secondaryOperation;
            try
            {
                return await operation(owner).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "[FallbackCaptureSourceRepository] Owning repository ({Type}) failed during {Operation} for {SourceId}.",
                    owner.GetType().Name,
                    operationName,
                    sourceId);
                return false;
            }
        }

        // Not seen in a merge yet — keep writing to both so nothing is lost.
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
