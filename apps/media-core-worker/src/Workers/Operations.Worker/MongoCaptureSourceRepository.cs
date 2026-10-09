using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The capture source catalogue: the `config.platforms` collection of the local MongoDB, the same
/// documents the web-api's Plataformas screen edits (a source is the document whose `sourceId` is
/// set). It is the only catalogue: there is no file fallback any more, and no cache either, since
/// the database is local and the collection small.
///
/// The worker only ever changes three fields of a source (streamUrl, fallbackStreamUrls and
/// isExcluded), each with a `$set` of that one field and only when the stored value differs — so
/// nothing the operator edits in the same document is overwritten, and a pass that keeps finding
/// a source excluded does not rewrite it every time.
/// </summary>
public sealed class MongoCaptureSourceRepository : ICaptureSourceRepository
{
    private readonly MongoConfigStore store;
    private readonly ILogger<MongoCaptureSourceRepository> logger;

    public MongoCaptureSourceRepository(MongoConfigStore store, ILogger<MongoCaptureSourceRepository> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var documents = await store.Platforms
            .Find(Builders<BsonDocument>.Filter.Exists("sourceId"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sources = documents
            .Select(MongoDocumentMapper.TryMapCaptureSource)
            .Where(source => source is not null)
            .Select(source => source!)
            .ToArray();

        logger.LogDebug("[MongoCaptureSourceRepository] Loaded {Count} capture source(s) from MongoDB.", sources.Length);
        return sources;
    }

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(streamUrl))
        {
            return Task.FromResult(false);
        }

        return SetIfChangedAsync(sourceId, "streamUrl", streamUrl, Builders<BsonDocument>.Filter.Ne("streamUrl", streamUrl), cancellationToken);
    }

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || fallbackStreamUrls is null)
        {
            return Task.FromResult(false);
        }

        var cleaned = new BsonArray(fallbackStreamUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        return SetIfChangedAsync(sourceId, "fallbackStreamUrls", cleaned, Builders<BsonDocument>.Filter.Ne("fallbackStreamUrls", cleaned), cancellationToken);
    }

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return Task.FromResult(false);
        }

        // A source with no flag counts as not excluded, so clearing it only writes when it was set.
        var differs = excluded
            ? Builders<BsonDocument>.Filter.Ne("isExcluded", true)
            : Builders<BsonDocument>.Filter.Eq("isExcluded", true);

        return SetIfChangedAsync(sourceId, "isExcluded", excluded, differs, cancellationToken);
    }

    // True when the source exists (whether or not its stored value had to change).
    private async Task<bool> SetIfChangedAsync(
        string sourceId, string field, BsonValue value, FilterDefinition<BsonDocument> differs, CancellationToken cancellationToken)
    {
        var bySource = Builders<BsonDocument>.Filter.Eq("sourceId", sourceId);
        var result = await store.Platforms
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(bySource, differs),
                Builders<BsonDocument>.Update.Set(field, value),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.MatchedCount > 0)
        {
            return true;
        }

        var exists = await store.Platforms.CountDocumentsAsync(bySource, cancellationToken: cancellationToken).ConfigureAwait(false) > 0;
        if (!exists)
        {
            logger.LogWarning("[MongoCaptureSourceRepository] No platform with sourceId '{SourceId}' — cannot update '{Field}'.", sourceId, field);
        }

        return exists;
    }
}
