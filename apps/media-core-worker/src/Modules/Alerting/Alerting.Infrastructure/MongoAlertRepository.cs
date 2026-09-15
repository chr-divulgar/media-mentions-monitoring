using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// Writes the same document shape as Helper.InsertAlert (media-monitor/apps/w-service/Helper.cs:469-472)
// so a future cutover (pointing AlertCollectionName at "alert" instead of "workerAlert") needs no data
// transform. Dedup lookups are scoped to this same collection only — see MongoAlertingOptions.
public sealed class MongoAlertRepository : IAlertRepository
{
    private static readonly string[] RepeatCheckTypes = { "Nueva", "RepetidaOtraPlataforma" };

    private readonly IMongoCollection<BsonDocument> alertCollection;

    public MongoAlertRepository(MongoAlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var database = new MongoClient(options.ConnectionString).GetDatabase(options.MonitoringDatabaseName);
        alertCollection = database.GetCollection<BsonDocument>(options.AlertCollectionName);
    }

    public Task InsertAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var document = new BsonDocument
        {
            { "text", alert.Text },
            { "startTime", alert.StartTime.UtcDateTime },
            { "endTime", alert.EndTime.UtcDateTime },
            { "media", alert.Media },
            { "platform", alert.Platform },
            { "filePath", alert.FilePath },
            { "words", new BsonArray(alert.Words) },
            { "clientName", alert.ClientName },
            { "type", alert.Type.ToLegacyLabel() }
        };

        return alertCollection.InsertOneAsync(document, options: null, cancellationToken);
    }

    public async Task<Alert?> GetLastNewOrRepeatedAlertAsync(string platform, string clientName, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("platform", platform),
            Builders<BsonDocument>.Filter.Eq("clientName", clientName),
            Builders<BsonDocument>.Filter.In("type", RepeatCheckTypes));
        var sort = Builders<BsonDocument>.Sort.Descending("$natural");

        var document = await alertCollection.Find(filter).Sort(sort).Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return document is null ? null : ToAlert(document);
    }

    private static Alert ToAlert(BsonDocument document)
    {
        return new Alert(
            document["text"].AsString,
            document["startTime"].ToUniversalTime(),
            document["endTime"].ToUniversalTime(),
            document["media"].AsString,
            document["platform"].AsString,
            document.TryGetValue("filePath", out var filePath) ? filePath.AsString : string.Empty,
            document["words"].AsBsonArray.Select(word => word.AsString).ToArray(),
            document["clientName"].AsString,
            AlertTypeExtensions.FromLegacyLabel(document["type"].AsString));
    }
}
