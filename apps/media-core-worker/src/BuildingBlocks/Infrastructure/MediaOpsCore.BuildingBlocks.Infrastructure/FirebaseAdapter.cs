using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.BuildingBlocks.Infrastructure;

public sealed class FirebaseAdapter : IMonitoringArtifactRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly HttpClient httpClient;
    private readonly FirebaseAdapterOptions options;

    public FirebaseAdapter(HttpClient httpClient, FirebaseAdapterOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        using var request = new HttpRequestMessage(HttpMethod.Put, BuildDocumentUri(artifact.Id));
        request.Content = JsonContent.Create(artifact, options: SerializerOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        }

        using var response = await httpClient.GetAsync(BuildDocumentUri(id), cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MonitoringArtifact>(SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(tenantId));
        }

        using var response = await httpClient.GetAsync(BuildCollectionUri(), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var documents = await response.Content.ReadFromJsonAsync<Dictionary<string, MonitoringArtifact>>(SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (documents is null || documents.Count == 0)
        {
            return Array.Empty<MonitoringArtifact>();
        }

        return documents.Values.Where(document => string.Equals(document.TenantId, tenantId, StringComparison.Ordinal)).ToArray();
    }

    private Uri BuildCollectionUri()
    {
        return BuildUri(string.Empty);
    }

    private Uri BuildDocumentUri(string documentId)
    {
        return BuildUri($"{Uri.EscapeDataString(documentId)}.json");
    }

    private Uri BuildUri(string suffix)
    {
        var baseUri = new Uri(options.BaseUrl.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        var relativePath = string.IsNullOrWhiteSpace(suffix)
            ? $"{options.RootPath}.json"
            : $"{options.RootPath}/{suffix}";

        var uri = new Uri(baseUri, relativePath);

        if (string.IsNullOrWhiteSpace(options.AuthToken))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Query = $"auth={Uri.EscapeDataString(options.AuthToken)}"
        };

        return builder.Uri;
    }
}