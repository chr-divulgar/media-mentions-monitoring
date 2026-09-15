using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// Reads the same config.client / config.platform collections already used in production by
// media-monitor/apps/w-service/Helper.cs:452-462 — read-only, shared, no risk of contaminating them.
public sealed class MongoClientConfigRepository : IClientConfigRepository
{
    private readonly IMongoCollection<BsonDocument> clientCollection;
    private readonly IMongoCollection<BsonDocument> platformCollection;

    public MongoClientConfigRepository(MongoAlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var database = new MongoClient(options.ConnectionString).GetDatabase(options.ConfigDatabaseName);
        clientCollection = database.GetCollection<BsonDocument>("client");
        platformCollection = database.GetCollection<BsonDocument>("platform");
    }

    public async Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await clientCollection.Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents.Select(ToClientKeywordConfig).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default)
    {
        var documents = await platformCollection.Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents.Select(document => document["name"].AsString).ToArray();
    }

    private static ClientKeywordConfig ToClientKeywordConfig(BsonDocument document)
    {
        var keywords = document["words"].AsBsonArray
            .Select(word => ToKeywordConfig(word.AsBsonDocument))
            .ToArray();

        return new ClientKeywordConfig(
            document["name"].AsString,
            keywords,
            ReadStringArray(document, "numbers_radio"),
            ReadStringArray(document, "numbers_tv"));
    }

    private static KeywordConfig ToKeywordConfig(BsonDocument word)
    {
        var adds = word.TryGetValue("adds", out var addsValue) && addsValue.IsBsonArray
            ? addsValue.AsBsonArray.Select(add => ToAdContext(add.AsBsonDocument)).ToArray()
            : Array.Empty<KeywordAdContext>();

        return new KeywordConfig(word["value"].AsString, adds);
    }

    private static KeywordAdContext ToAdContext(BsonDocument add)
    {
        // "afer" is the real (misspelled) key stored in production config.client documents — read
        // verbatim, see media-monitor/apps/w-service/Helper.cs:340 and Alerting.Domain/ClientKeywordConfig.cs.
        return new KeywordAdContext(
            add.TryGetValue("before", out var before) ? before.AsString : string.Empty,
            add.TryGetValue("afer", out var after) ? after.AsString : string.Empty);
    }

    private static IReadOnlyList<string> ReadStringArray(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || !value.IsBsonArray)
        {
            return Array.Empty<string>();
        }

        return value.AsBsonArray.Select(item => item.ToString() ?? string.Empty).ToArray();
    }
}
