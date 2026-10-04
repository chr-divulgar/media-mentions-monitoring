using MediaOpsCore.Modules.Alerting.Application;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// One document per Bogota day that already got its digest, keyed by the date so a second insert
// for the same day is impossible rather than merely unlikely.
public sealed class MongoDailyDigestLog : IDailyDigestLog
{
    private const string CollectionName = "dailyDigest";

    private readonly IMongoCollection<BsonDocument> collection;

    public MongoDailyDigestLog(MongoAlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        collection = new MongoClient(options.ConnectionString)
            .GetDatabase(options.MonitoringDatabaseName)
            .GetCollection<BsonDocument>(CollectionName);
    }

    public async Task<bool> WasSentAsync(DateOnly day, CancellationToken cancellationToken = default) =>
        await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", Key(day))).AnyAsync(cancellationToken).ConfigureAwait(false);

    public async Task MarkSentAsync(DateOnly day, int clientsNotified, CancellationToken cancellationToken = default)
    {
        var document = new BsonDocument
        {
            { "_id", Key(day) },
            { "sentAt", DateTime.UtcNow },
            { "clientsNotified", clientsNotified },
        };

        await collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", Key(day)),
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd");
}
