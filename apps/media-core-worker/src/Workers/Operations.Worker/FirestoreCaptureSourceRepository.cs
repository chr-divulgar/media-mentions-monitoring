using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads/writes the capture source catalog from the same Firestore "platforms" collection
/// apps/web-api/src/app/settings/settings.service.ts already manages (fields sourceId, name,
/// media, streamUrl, primaryUrl, country, fallbackStreamUrls, isExcluded — see
/// packages/shared/models/settings.dto.ts). A document with no sourceId is a WhatsApp-only
/// platform entry unrelated to capture and is skipped.
///
/// Firestore's REST API wraps every field value in a type tag (e.g. {"stringValue": "..."}) and
/// only exposes a document's own auto-generated id, not the sourceId field — so updates first run
/// a structured query to resolve sourceId -> document id, then PATCH that document with an
/// updateMask so only the one field changes.
/// </summary>
public sealed class FirestoreCaptureSourceRepository : ICaptureSourceRepository
{
    private readonly HttpClient httpClient;
    private readonly GoogleServiceAccountTokenProvider tokenProvider;
    private readonly FirestoreCaptureSourceRepositoryOptions options;
    private readonly ILogger<FirestoreCaptureSourceRepository> logger;
    private readonly string documentsRootUri;

    // The source catalog is read on every /capture/status request (55 documents a time, and the
    // status page refreshes over a socket), which is enough to burn a day of Firestore read quota
    // in an afternoon. The catalog changes only when someone edits a platform or the worker
    // excludes a source — both of which invalidate this — so serving it from a short-lived cache
    // costs nothing in freshness.
    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private IReadOnlyList<CaptureSource>? cachedSources;
    private DateTimeOffset cachedSourcesAt;

    public FirestoreCaptureSourceRepository(
        HttpClient httpClient,
        GoogleServiceAccountTokenProvider tokenProvider,
        FirestoreCaptureSourceRepositoryOptions options,
        ILogger<FirestoreCaptureSourceRepository> logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        documentsRootUri = $"https://firestore.googleapis.com/v1/projects/{options.ProjectId}/databases/(default)/documents";
    }

    public async Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetCachedSources(out var cached))
        {
            return cached;
        }

        await catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed it while this one waited.
            if (TryGetCachedSources(out cached))
            {
                return cached;
            }

            var sources = await FetchAllAsync(cancellationToken).ConfigureAwait(false);
            cachedSources = sources;
            cachedSourcesAt = DateTimeOffset.UtcNow;
            return sources;
        }
        finally
        {
            catalogGate.Release();
        }
    }

    private bool TryGetCachedSources(out IReadOnlyList<CaptureSource> sources)
    {
        var snapshot = cachedSources;
        if (snapshot is not null && DateTimeOffset.UtcNow - cachedSourcesAt < CatalogCacheDuration)
        {
            sources = snapshot;
            return true;
        }

        sources = Array.Empty<CaptureSource>();
        return false;
    }

    private void InvalidateCatalogCache() => cachedSources = null;

    private async Task<IReadOnlyList<CaptureSource>> FetchAllAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{documentsRootUri}/{options.CollectionPath}?pageSize=300");
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("documents", out var documents))
        {
            logger.LogWarning(
                "[FirestoreCaptureSourceRepository] Collection '{Collection}' returned no documents.",
                options.CollectionPath);
            return Array.Empty<CaptureSource>();
        }

        var sources = documents.EnumerateArray()
            .Select(FirestoreDocumentMapper.TryMapCaptureSource)
            .Where(source => source is not null)
            .Select(source => source!)
            .ToArray();

        logger.LogInformation(
            "[FirestoreCaptureSourceRepository] Loaded {Count} capture source(s) from Firestore collection '{Collection}'.",
            sources.Length, options.CollectionPath);

        return sources;
    }

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(streamUrl))
        {
            return Task.FromResult(false);
        }

        return PatchFieldAsync(sourceId, "streamUrl", FirestoreDocumentMapper.StringValue(streamUrl), cancellationToken);
    }

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || fallbackStreamUrls is null)
        {
            return Task.FromResult(false);
        }

        var cleaned = fallbackStreamUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return PatchFieldAsync(sourceId, "fallbackStreamUrls", FirestoreDocumentMapper.StringArrayValue(cleaned), cancellationToken);
    }

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return Task.FromResult(false);
        }

        return PatchFieldAsync(sourceId, "isExcluded", FirestoreDocumentMapper.BoolValue(excluded), cancellationToken);
    }

    private async Task<string?> FindDocumentIdAsync(string sourceId, CancellationToken cancellationToken)
    {
        var queryBody = new
        {
            structuredQuery = new
            {
                from = new[] { new { collectionId = options.CollectionPath } },
                where = new
                {
                    fieldFilter = new
                    {
                        field = new { fieldPath = "sourceId" },
                        op = "EQUAL",
                        value = new { stringValue = sourceId },
                    },
                },
                limit = 1,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{documentsRootUri}:runQuery")
        {
            Content = JsonContent.Create(queryBody),
        };
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token)
            .ConfigureAwait(false);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("document", out var matchedDocument) &&
                matchedDocument.TryGetProperty("name", out var nameElement))
            {
                return nameElement.GetString()?.Split('/').LastOrDefault();
            }
        }

        return null;
    }

    private async Task<bool> PatchFieldAsync(string sourceId, string fieldName, object firestoreValue, CancellationToken cancellationToken)
    {
        var documentId = await FindDocumentIdAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (documentId is null)
        {
            logger.LogWarning(
                "[FirestoreCaptureSourceRepository] No document found for sourceId '{SourceId}' — cannot update '{Field}'.",
                sourceId, fieldName);
            return false;
        }

        var body = new { fields = new Dictionary<string, object> { [fieldName] = firestoreValue } };
        var uri = $"{documentsRootUri}/{options.CollectionPath}/{documentId}?updateMask.fieldPaths={fieldName}";

        using var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = JsonContent.Create(body) };
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        InvalidateCatalogCache();
        return true;
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
