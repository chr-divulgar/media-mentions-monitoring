using MediaOpsCore.Modules.Alerting.Infrastructure;
using MediaOpsCore.Modules.Capture.Application;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// One document per discovered web-news article, alerted or not — the "guardar el enlace en la
/// base de datos" half of the flow. Reuses the same MongoDB connection/database as alerts
/// (MongoAlertingOptions) in a sibling collection, since this is the same monitoring database.
/// </summary>
public sealed class MongoWebArticleRepository : IWebArticleRepository
{
    private const string CollectionName = "webArticle";

    private readonly IMongoCollection<BsonDocument> collection;
    private readonly int maxFailedAttempts;
    private int indexEnsured;

    public MongoWebArticleRepository(MongoAlertingOptions options, int maxFailedAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);
        var database = new MongoClient(options.ConnectionString).GetDatabase(options.MonitoringDatabaseName);
        collection = database.GetCollection<BsonDocument>(CollectionName);
        this.maxFailedAttempts = maxFailedAttempts;
    }

    public async Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        // A "failed" article (timeout, HTTP error, unreadable page) stays eligible for retry on a
        // later cycle instead of being treated as done for the rest of the lookback window — some
        // failures are transient. Once it reaches the attempt cap it is excluded like any other
        // resolved article, so a site that blocks every request isn't retried for a full day.
        // Records saved before this field existed have no attemptCount at all; Gte on a missing
        // field never matches, which correctly keeps retrying them rather than skipping them.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Gte("discoveredAt", DateTime.UtcNow - lookback),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Ne("status", WebArticleStatus.Failed),
                Builders<BsonDocument>.Filter.Gte("attemptCount", maxFailedAttempts)));

        var documents = await collection.Find(filter).Project(Builders<BsonDocument>.Projection.Include("googleId"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return documents.Select(document => document["googleId"].AsString).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        var filter = Builders<BsonDocument>.Filter.Eq("googleId", record.GoogleId);

        if (record.Status == WebArticleStatus.Failed)
        {
            // $inc on a field that doesn't exist yet (first failure, or an upsert creating the
            // document) starts it at the increment amount — attemptCount reaches 1 here, not 0.
            var update = Builders<BsonDocument>.Update
                .Set("googleId", record.GoogleId)
                .Set("url", record.Url)
                .Set("outlet", record.Outlet)
                .Set("title", record.Title ?? (BsonValue)BsonNull.Value)
                .Set("firstParagraph", record.FirstParagraph ?? (BsonValue)BsonNull.Value)
                .Set("publishedAt", record.PublishedAt?.UtcDateTime ?? (BsonValue)BsonNull.Value)
                .Set("status", record.Status)
                .Set("matchedKeywords", new BsonArray(record.MatchedKeywords))
                .Set("clients", new BsonArray(record.Clients))
                .Set("discoveredAt", record.DiscoveredAt.UtcDateTime)
                .Inc("attemptCount", 1);

            var updated = await collection.FindOneAndUpdateAsync(
                filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return updated["attemptCount"].ToInt32() >= maxFailedAttempts;
        }

        // A terminal outcome (alerted, or a content-based rejection like no-date/no-keyword)
        // replaces the document wholesale — any earlier failed attempts for this id no longer
        // matter once it resolves, so attemptCount resets rather than carrying over.
        var document = new BsonDocument
        {
            { "googleId", record.GoogleId },
            { "url", record.Url },
            { "outlet", record.Outlet },
            { "title", record.Title ?? (BsonValue)BsonNull.Value },
            { "firstParagraph", record.FirstParagraph ?? (BsonValue)BsonNull.Value },
            { "publishedAt", record.PublishedAt?.UtcDateTime ?? (BsonValue)BsonNull.Value },
            { "status", record.Status },
            { "matchedKeywords", new BsonArray(record.MatchedKeywords) },
            { "clients", new BsonArray(record.Clients) },
            { "discoveredAt", record.DiscoveredAt.UtcDateTime },
            { "attemptCount", 0 },
        };

        await collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Lazy, not constructor-time: index creation is I/O the host shouldn't block startup on, and
    // this repository is only even registered when web-news is enabled.
    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref indexEnsured, 1) == 1)
        {
            return;
        }

        try
        {
            var keys = Builders<BsonDocument>.IndexKeys.Ascending("googleId");
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A missing index only costs a collection scan at this volume — never worth failing
            // the save over.
            Interlocked.Exchange(ref indexEnsured, 0);
        }
    }
}
