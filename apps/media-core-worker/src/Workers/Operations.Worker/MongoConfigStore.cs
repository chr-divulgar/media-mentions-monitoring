using MediaOpsCore.Modules.Alerting.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The collections of the local MongoDB the worker reads its configuration from, one shared
/// client: `config.platforms` (the capture sources), `config.clients` (keywords and recipients),
/// `config.users` (the backup copy of the users, for who is told about platform errors) and
/// `monitoring.captureHours` (the hourly coverage audit trail). apps/web-api writes the first
/// three through its settings screens; this replaced the Firestore catalogue and the JSON file.
/// </summary>
public sealed class MongoConfigStore
{
    private static readonly TimeSpan ReadyRetryInterval = TimeSpan.FromSeconds(60);

    private readonly IMongoDatabase config;
    private readonly IMongoDatabase monitoring;

    public MongoConfigStore(MongoAlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new MongoClient(options.ConnectionString);
        config = client.GetDatabase(options.ConfigDatabaseName);
        monitoring = client.GetDatabase(options.MonitoringDatabaseName);
    }

    public IMongoCollection<BsonDocument> Platforms => config.GetCollection<BsonDocument>("platforms");

    public IMongoCollection<BsonDocument> Clients => config.GetCollection<BsonDocument>("clients");

    public IMongoCollection<BsonDocument> Users => config.GetCollection<BsonDocument>("users");

    public IMongoCollection<BsonDocument> CaptureHours => monitoring.GetCollection<BsonDocument>("captureHours");

    /// <summary>
    /// Waits until MongoDB answers, retrying every minute. At boot the database service may not be
    /// up yet; the catalogue read that follows would otherwise fail (and every source with it).
    /// </summary>
    public async Task WaitUntilReadyAsync(ILogger logger, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                await config.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "[MongoConfigStore] MongoDB is not answering yet ({Reason}); retrying in {Seconds:0} s.",
                    exception.Message.Split('\n')[0], ReadyRetryInterval.TotalSeconds);
                await Task.Delay(ReadyRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
