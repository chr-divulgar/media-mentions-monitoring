using MediaOpsCore.Modules.Alerting.Infrastructure;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>What an index of the transcription collection is, apart from the driver's types, so it can be checked.</summary>
public sealed record TranscriptionIndex(string Name, IReadOnlyList<string> Keys, bool Unique, TimeSpan? ExpireAfter, BsonDocument PartialFilter);

/// <summary>
/// The transcriptions in the `transcription` collection of the monitoring database. That collection already holds 3
/// million documents of the old system (TV, October) with the same names for text, times, media, platform and file: the
/// new ones keep those names and add sourceId and status. Every index is limited to documents that have a sourceId, so
/// the old ones are in none of them (and never expire).
/// </summary>
public sealed class MongoTranscriptionStore : ITranscriptionStore
{
    private const string CollectionName = "transcription";
    private const string HasSourceId = "sourceId";

    private readonly IMongoCollection<BsonDocument> collection;
    private readonly int retentionDays;
    private readonly ILogger<MongoTranscriptionStore>? logger;
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private bool indexesEnsured;

    public MongoTranscriptionStore(MongoAlertingOptions options, int retentionDays, ILogger<MongoTranscriptionStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        collection = new MongoClient(options.ConnectionString).GetDatabase(options.MonitoringDatabaseName).GetCollection<BsonDocument>(CollectionName);
        this.retentionDays = retentionDays;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<TranscriptionEntry>> LoadHourAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("filePath", filePath),
            Builders<BsonDocument>.Filter.Exists(HasSourceId));
        var documents = await collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("startTime"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToEntry).ToArray();
    }

    public async Task AppendAsync(TranscriptionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        await collection.ReplaceOneAsync(
            new BsonDocumentFilterDefinition<BsonDocument>(KeyFilter(entry)),
            ToDocument(entry, DateTime.UtcNow),
            new ReplaceOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendManyAsync(IReadOnlyList<TranscriptionEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var writes = entries.Select(entry => new ReplaceOneModel<BsonDocument>(
            new BsonDocumentFilterDefinition<BsonDocument>(KeyFilter(entry)), ToDocument(entry, now)) { IsUpsert = true });
        await collection.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The key of an entry: its hour file and its times. It also says "has a sourceId": the indexes only cover those
    /// documents, and MongoDB uses a partial index only for a filter that implies its own. Without it every save read the
    /// 3 million documents of the old system.
    /// </summary>
    public static BsonDocument KeyFilter(TranscriptionEntry entry) => new()
    {
        { "filePath", entry.FilePath },
        { "startTime", entry.StartTime.UtcDateTime },
        { "endTime", entry.EndTime.UtcDateTime },
        { HasSourceId, new BsonDocument("$exists", true) },
    };

    public static BsonDocument ToDocument(TranscriptionEntry entry, DateTime createdAtUtc) => new()
    {
        { "sourceId", entry.SourceId },
        { "platform", entry.Platform },
        { "media", entry.Media },
        { "filePath", entry.FilePath },
        { "startTime", entry.StartTime.UtcDateTime },
        { "endTime", entry.EndTime.UtcDateTime },
        { "text", entry.Text },
        { "status", entry.Status },
        { "createdAt", createdAtUtc },
    };

    public static TranscriptionEntry ToEntry(BsonDocument document) => new(
        document.GetValue("sourceId", "").AsString,
        document.GetValue("platform", "").AsString,
        document.GetValue("media", "").AsString,
        document.GetValue("filePath", "").AsString,
        new DateTimeOffset(document["startTime"].ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(document["endTime"].ToUniversalTime(), TimeSpan.Zero),
        document.GetValue("text", "").AsString,
        document.GetValue("status", "recognized").AsString);

    public static IReadOnlyList<TranscriptionIndex> IndexDefinitions(int retentionDays)
    {
        var onlyNew = new BsonDocument(HasSourceId, new BsonDocument("$exists", true));
        return
        [
            // Saves by hour file and times without duplicating, and loads the hour a chunk belongs to.
            new("transcription_hour_unique", ["filePath", "startTime", "endTime"], Unique: true, ExpireAfter: null, onlyNew),
            // The text is kept for the retention period, counted from the time of the audio.
            new("transcription_expiry", ["startTime"], Unique: false, ExpireAfter: TimeSpan.FromDays(retentionDays), onlyNew),
        ];
    }

    // Lazy, and once: on the first save or load. Building an index over 3 million documents takes a while, and
    // it must not hold up the start of the service. Without the indexes the store still works (slower), so a failure here is
    // reported and does not stop the transcriptions.
    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (indexesEnsured)
        {
            return;
        }

        await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (indexesEnsured)
            {
                return;
            }

            foreach (var index in IndexDefinitions(retentionDays))
            {
                var keys = new BsonDocument(index.Keys.Select(key => new BsonElement(key, 1)));
                var model = new CreateIndexModel<BsonDocument>(
                    new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = index.Name,
                        Unique = index.Unique,
                        ExpireAfter = index.ExpireAfter,
                        PartialFilterExpression = new BsonDocumentFilterDefinition<BsonDocument>(index.PartialFilter),
                    });
                try
                {
                    await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (MongoCommandException exception)
                {
                    logger?.LogError(exception, "[MongoTranscriptionStore] Could not create the index {Index}; transcriptions are saved without it.", index.Name);
                }
            }

            indexesEnsured = true;
        }
        finally
        {
            indexGate.Release();
        }
    }
}
