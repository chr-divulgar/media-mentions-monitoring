using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The clients (keywords, alert levels, recipients per media) from the `config.clients` collection
/// of the local MongoDB, the documents apps/web-api's clients screen edits. Read on every alert
/// detection, so the list is kept for a short while; a failed read is not remembered.
///
/// Platform names for the "repeated on another platform" check come from ICaptureSourceProvider
/// instead of a second query: restricting it to the platforms this worker actually monitors is
/// more correct than an independent platform list.
/// </summary>
public sealed class MongoClientConfigRepository : IClientConfigRepository
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private readonly MongoConfigStore store;
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly ILogger<MongoClientConfigRepository> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ClientKeywordConfig>? cached;
    private DateTimeOffset cachedAt;

    public MongoClientConfigRepository(
        MongoConfigStore store,
        ICaptureSourceProvider captureSourceProvider,
        ILogger<MongoClientConfigRepository> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.captureSourceProvider = captureSourceProvider ?? throw new ArgumentNullException(nameof(captureSourceProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetCached(out var fresh))
        {
            return fresh;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out fresh))
            {
                return fresh;
            }

            var documents = await store.Clients
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var clients = documents
                .Select(MongoDocumentMapper.TryMapClient)
                .Where(client => client is not null)
                .Select(client => client!)
                .ToArray();

            logger.LogInformation("[MongoClientConfigRepository] Loaded {Count} client(s) from MongoDB.", clients.Length);
            cached = clients;
            cachedAt = DateTimeOffset.UtcNow;
            return clients;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);
        return sources.Select(source => source.Platform).Distinct(StringComparer.Ordinal).ToArray();
    }

    private bool TryGetCached(out IReadOnlyList<ClientKeywordConfig> clients)
    {
        var snapshot = cached;
        if (snapshot is not null && DateTimeOffset.UtcNow - cachedAt < CacheDuration)
        {
            clients = snapshot;
            return true;
        }

        clients = [];
        return false;
    }
}
