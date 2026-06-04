using System.Net.Http.Json;
using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StageMirrorMonitoringArtifactRepository : IMonitoringArtifactRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly InMemoryMonitoringArtifactRepository inMemoryRepository;
    private readonly HttpClient httpClient;
    private readonly OperationsWorkerOptions options;

    public StageMirrorMonitoringArtifactRepository(
        InMemoryMonitoringArtifactRepository inMemoryRepository,
        HttpClient httpClient,
        OperationsWorkerOptions options)
    {
        this.inMemoryRepository = inMemoryRepository;
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
    {
        await inMemoryRepository.UpsertAsync(artifact, cancellationToken).ConfigureAwait(false);

        if (!options.EnableStageDatabaseMirror)
        {
            return;
        }

        if (!Uri.TryCreate(options.StageDatabaseBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return;
        }

        var rootPath = string.IsNullOrWhiteSpace(options.StageDatabaseRootPath)
            ? "monitoringArtifacts"
            : options.StageDatabaseRootPath.Trim('/');

        var relative = $"{rootPath}/{Uri.EscapeDataString(artifact.Id)}.json";
        var uri = new Uri(baseUri.ToString().TrimEnd('/') + "/" + relative);

        if (!string.IsNullOrWhiteSpace(options.StageDatabaseAuthToken))
        {
            var builder = new UriBuilder(uri)
            {
                Query = $"auth={Uri.EscapeDataString(options.StageDatabaseAuthToken)}"
            };
            uri = builder.Uri;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = JsonContent.Create(artifact, options: SerializerOptions)
        };

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Stage mirror errors should not stop worker cycle; in-memory store remains source for pipeline continuity.
        }
    }

    public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        return inMemoryRepository.GetAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        return inMemoryRepository.ListByTenantAsync(tenantId, cancellationToken);
    }
}