using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Where the hourly capture coverage audit trail is kept.</summary>
public interface ICaptureSummaryStore
{
    /// <summary>Creates or overwrites the record of one clock hour (the hour key is its identity).</summary>
    Task UpsertHourAsync(string hourKey, CaptureSummarySnapshot snapshot, bool isClosed, CancellationToken cancellationToken = default);
}

/// <summary>
/// Audit trail for capture coverage: one document per clock hour in `monitoring.captureHours`, id =
/// the hour key ("2026-09-17_13"). The in-progress hour is refreshed periodically and then
/// overwritten with the authoritative numbers when the hour closes, so stopping the worker no longer
/// erases what it had recorded. The per-source detail travels as one JSON string field.
/// </summary>
public sealed class MongoCaptureSummaryStore : ICaptureSummaryStore
{
    private readonly MongoConfigStore store;
    private readonly ILogger<MongoCaptureSummaryStore> logger;

    public MongoCaptureSummaryStore(MongoConfigStore store, ILogger<MongoCaptureSummaryStore> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UpsertHourAsync(string hourKey, CaptureSummarySnapshot snapshot, bool isClosed, CancellationToken cancellationToken = default)
    {
        var document = new BsonDocument
        {
            { "_id", hourKey },
            { "hourKey", hourKey },
            { "updatedAtUtc", DateTime.UtcNow },
            { "isClosed", isClosed },
            { "sourceCount", snapshot.Sources.Length },
            { "averageCoveragePercent", snapshot.Sources.Length == 0 ? 0 : Math.Round(snapshot.Sources.Average(source => source.CoveragePercent), 2) },
            { "snapshotJson", JsonSerializer.Serialize(snapshot) },
        };

        try
        {
            await store.CaptureHours
                .ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", hourKey),
                    document,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[MongoCaptureSummaryStore] Could not persist capture summary for hour {HourKey}.", hourKey);
        }
    }
}
