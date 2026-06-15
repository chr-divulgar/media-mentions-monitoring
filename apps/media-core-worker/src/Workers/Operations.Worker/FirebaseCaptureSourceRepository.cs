using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads the capture source catalog from Firebase Realtime Database.
/// Fetches the platforms node via REST GET and maps the dictionary response to domain objects.
/// Field names in Firebase must match the worker schema: sourceId, platform, media, streamUrl, etc.
/// </summary>
public sealed class FirebaseCaptureSourceRepository : ICaptureSourceRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Mirrors the Firebase document schema. Legacy fields (city, zone, slots) are intentionally
    // omitted — the deserializer ignores unknown properties by default.
    private sealed record PlatformDocument(
        string SourceId,
        string Platform,
        string Media,
        string StreamUrl,
        string? PrimaryUrl,
        string? Country,
        IReadOnlyList<string>? FallbackStreamUrls,
        bool? Excluded);

    private readonly HttpClient httpClient;
    private readonly FirebaseCaptureSourceRepositoryOptions options;
    private readonly ILogger<FirebaseCaptureSourceRepository> logger;

    public FirebaseCaptureSourceRepository(
        HttpClient httpClient,
        FirebaseCaptureSourceRepositoryOptions options,
        ILogger<FirebaseCaptureSourceRepository> logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var uri = BuildUri();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        logger.LogDebug("[FirebaseCaptureSourceRepository] Fetching platforms from {Uri}", uri);

        using var response = await httpClient.GetAsync(uri, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var documents = await response.Content
            .ReadFromJsonAsync<Dictionary<string, PlatformDocument>>(SerializerOptions, cts.Token)
            .ConfigureAwait(false);

        if (documents is null || documents.Count == 0)
        {
            logger.LogWarning(
                "[FirebaseCaptureSourceRepository] Firebase returned an empty or null platforms node at path '{Path}'.",
                options.PlatformsPath);
            return Array.Empty<CaptureSource>();
        }

        var sources = documents.Values
            .Select(doc => new CaptureSource(
                doc.SourceId,
                JsonFileCaptureSourceRepository.GlobalIngestionScopeId,
                doc.Platform,
                doc.Media,
                doc.StreamUrl,
                doc.PrimaryUrl,
                doc.Country,
                fallbackStreamUrls: doc.FallbackStreamUrls,
                isExcluded: doc.Excluded ?? false))
            .ToArray();

        logger.LogInformation(
            "[FirebaseCaptureSourceRepository] Loaded {Count} source(s) from Firebase.",
            sources.Length);

        return sources;
    }

    private string BuildUri()
    {
        var uri = $"{options.BaseUrl!.TrimEnd('/')}/{options.PlatformsPath}.json";
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            uri += $"?auth={Uri.EscapeDataString(options.AuthToken)}";
        return uri;
    }
}
