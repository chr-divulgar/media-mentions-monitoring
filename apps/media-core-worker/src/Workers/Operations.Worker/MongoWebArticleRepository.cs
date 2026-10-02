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
    private int indexEnsured;

    public MongoWebArticleRepository(MongoAlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var database = new MongoClient(options.ConnectionString).GetDatabase(options.MonitoringDatabaseName);
        collection = database.GetCollection<BsonDocument>(CollectionName);
    }

    public async Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.Gte("discoveredAt", DateTime.UtcNow - lookback);
        var documents = await collection.Find(filter).Project(Builders<BsonDocument>.Projection.Include("googleId"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return documents.Select(document => document["googleId"].AsString).ToHashSet(StringComparer.Ordinal);
    }

    public async Task SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

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
        };

        await collection.InsertOneAsync(document, options: null, cancellationToken).ConfigureAwait(false);
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
