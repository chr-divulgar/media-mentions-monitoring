using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Registered instead of FirestoreClientConfigRepository when Firestore isn't configured
/// (options.Firestore is null/disabled). Returns nothing rather than silently falling back to
/// the orphaned legacy Mongo config.client/config.platform collections — alert detection simply
/// finds no keyword matches until Firestore credentials are provided, logged once so it isn't a
/// silent no-op.
/// </summary>
public sealed class NullClientConfigRepository : IClientConfigRepository
{
    private readonly ILogger<NullClientConfigRepository> logger;
    private int warned;

    public NullClientConfigRepository(ILogger<NullClientConfigRepository> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        WarnOnce();
        return Task.FromResult<IReadOnlyList<ClientKeywordConfig>>(Array.Empty<ClientKeywordConfig>());
    }

    public Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    private void WarnOnce()
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            logger.LogWarning(
                "[NullClientConfigRepository] Firestore is not configured (FIREBASE_PROJECT_ID/FIREBASE_CLIENT_EMAIL/FIREBASE_PRIVATE_KEY) — alert detection will find no client keyword matches until it is.");
        }
    }
}
