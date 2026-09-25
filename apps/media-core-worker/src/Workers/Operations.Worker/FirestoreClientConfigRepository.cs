using System.Net.Http.Headers;
using System.Text.Json;
using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads the same Firestore "clients" collection apps/web-api/src/app/clients/clients.service.ts
/// already manages (fields name, words[].value/adds[].before|after, alerts — see
/// packages/shared/models/clients.dto.ts) — the config operators actually edit today, via
/// apps/web-ui's Settings page. Same REST + service-account JWT pattern as
/// FirestoreCaptureSourceRepository, reusing the same GoogleServiceAccountTokenProvider.
///
/// Platform names for the "repeated on another platform" dedup check (Monitor.cs's
/// CheckIfDuplicateOtherPlatform) come from ICaptureSourceProvider instead of a second Firestore
/// query — it already reads the same "platforms" collection, and restricting the dedup check to
/// platforms this worker actually monitors is more correct than an independent platform list.
/// </summary>
public sealed class FirestoreClientConfigRepository : IClientConfigRepository
{
    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient httpClient;
    private readonly GoogleServiceAccountTokenProvider tokenProvider;
    private readonly FirestoreCaptureSourceRepositoryOptions options;
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly ILogger<FirestoreClientConfigRepository> logger;
    private readonly string documentsRootUri;
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private IReadOnlyList<ClientKeywordConfig>? cachedClients;
    private DateTimeOffset cachedClientsAt;

    public FirestoreClientConfigRepository(
        HttpClient httpClient,
        GoogleServiceAccountTokenProvider tokenProvider,
        FirestoreCaptureSourceRepositoryOptions options,
        ICaptureSourceProvider captureSourceProvider,
        ILogger<FirestoreClientConfigRepository> logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.captureSourceProvider = captureSourceProvider ?? throw new ArgumentNullException(nameof(captureSourceProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        documentsRootUri = $"https://firestore.googleapis.com/v1/projects/{options.ProjectId}/databases/(default)/documents";
    }

    public async Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetCachedClients(out var cached))
        {
            return cached;
        }

        await catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedClients(out cached))
            {
                return cached;
            }

            var clients = await FetchAllAsync(cancellationToken).ConfigureAwait(false);
            cachedClients = clients;
            cachedClientsAt = DateTimeOffset.UtcNow;
            return clients;
        }
        finally
        {
            catalogGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);
        return sources
            .Select(source => source.Platform)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private bool TryGetCachedClients(out IReadOnlyList<ClientKeywordConfig> clients)
    {
        var snapshot = cachedClients;
        if (snapshot is not null && DateTimeOffset.UtcNow - cachedClientsAt < CatalogCacheDuration)
        {
            clients = snapshot;
            return true;
        }

        clients = Array.Empty<ClientKeywordConfig>();
        return false;
    }

    private async Task<IReadOnlyList<ClientKeywordConfig>> FetchAllAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{documentsRootUri}/clients?pageSize=300");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        using var response = await httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false), cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("documents", out var documents))
        {
            logger.LogWarning("[FirestoreClientConfigRepository] Collection 'clients' returned no documents.");
            return Array.Empty<ClientKeywordConfig>();
        }

        var clients = documents.EnumerateArray()
            .Select(FirestoreClientDocumentMapper.TryMapClient)
            .Where(client => client is not null)
            .Select(client => client!)
            .ToArray();

        logger.LogInformation("[FirestoreClientConfigRepository] Loaded {Count} client(s) from Firestore collection 'clients'.", clients.Length);

        return clients;
    }
}
