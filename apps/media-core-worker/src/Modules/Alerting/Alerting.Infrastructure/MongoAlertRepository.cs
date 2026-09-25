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

    public async Task<string> InsertAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(recipients);

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
            { "type", alert.Type.ToLegacyLabel() },
            { "recipients", new BsonArray(recipients) },
            { "notifiedRecipients", new BsonArray() },
            { "fullyNotified", recipients.Count == 0 }
        };

        await alertCollection.InsertOneAsync(document, options: null, cancellationToken).ConfigureAwait(false);
        return document["_id"].AsObjectId.ToString();
    }

    public async Task MarkRecipientsNotifiedAsync(string alertId, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(alertId));
        var update = Builders<BsonDocument>.Update.AddToSetEach("notifiedRecipients", recipients);
        await alertCollection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Recompute fullyNotified from the just-updated document — a second round trip, but alert
        // volume is low enough that this is simpler and safer than an aggregation-pipeline update.
        var refreshed = await alertCollection.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return;
        }

        var intended = refreshed["recipients"].AsBsonArray.Select(v => v.AsString).ToHashSet(StringComparer.Ordinal);
        var notified = refreshed["notifiedRecipients"].AsBsonArray.Select(v => v.AsString).ToHashSet(StringComparer.Ordinal);

        await alertCollection.UpdateOneAsync(
            filter,
            Builders<BsonDocument>.Update.Set("fullyNotified", intended.IsSubsetOf(notified)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingAlertNotification>> GetPendingNotificationsAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("fullyNotified", false),
            Builders<BsonDocument>.Filter.Gte("endTime", DateTime.UtcNow - lookback));

        var documents = await alertCollection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<PendingAlertNotification>(documents.Count);
        foreach (var document in documents)
        {
            var intended = document["recipients"].AsBsonArray.Select(v => v.AsString).ToArray();
            var notified = document["notifiedRecipients"].AsBsonArray.Select(v => v.AsString).ToHashSet(StringComparer.Ordinal);
            var pending = intended.Where(number => !notified.Contains(number)).ToArray();

            if (pending.Length > 0)
            {
                results.Add(new PendingAlertNotification(document["_id"].AsObjectId.ToString(), ToAlert(document), pending));
            }
        }

        return results;
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
