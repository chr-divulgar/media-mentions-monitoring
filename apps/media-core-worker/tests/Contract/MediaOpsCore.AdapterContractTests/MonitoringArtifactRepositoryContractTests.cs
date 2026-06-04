using System.Net;
using System.Text;
using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.BuildingBlocks.Infrastructure;
using Xunit;

namespace MediaOpsCore.AdapterContractTests;

public sealed class MonitoringArtifactRepositoryContractTests
{
    public static TheoryData<Func<IMonitoringArtifactRepository>> Repositories =>
        new()
        {
            CreateFirebaseRepository,
            CreateInMemoryRepository
        };

    [Theory]
    [MemberData(nameof(Repositories))]
    public async Task Upsert_then_get_should_return_the_same_artifact(Func<IMonitoringArtifactRepository> repositoryFactory)
    {
        var repository = repositoryFactory();
        var artifact = CreateArtifact("artifact-1", "tenant-a");

        await repository.UpsertAsync(artifact);
        var storedArtifact = await repository.GetAsync(artifact.Id);

        Assert.NotNull(storedArtifact);
        Assert.Equal(artifact.Id, storedArtifact!.Id);
        Assert.Equal(artifact.TenantId, storedArtifact.TenantId);
        Assert.Equal(artifact.Source, storedArtifact.Source);
        Assert.Equal(artifact.Kind, storedArtifact.Kind);
    }

    [Theory]
    [MemberData(nameof(Repositories))]
    public async Task ListByTenant_should_only_return_records_for_the_requested_tenant(Func<IMonitoringArtifactRepository> repositoryFactory)
    {
        var repository = repositoryFactory();

        await repository.UpsertAsync(CreateArtifact("artifact-1", "tenant-a"));
        await repository.UpsertAsync(CreateArtifact("artifact-2", "tenant-b"));
        await repository.UpsertAsync(CreateArtifact("artifact-3", "tenant-a"));

        var tenantArtifacts = await repository.ListByTenantAsync("tenant-a");

        Assert.Equal(2, tenantArtifacts.Count);
        Assert.All(tenantArtifacts, artifact => Assert.Equal("tenant-a", artifact.TenantId));
    }

    private static IMonitoringArtifactRepository CreateFirebaseRepository()
    {
        var initialDocuments = new Dictionary<string, MonitoringArtifact>();
        var handler = new InMemoryFirebaseHandler(initialDocuments);

        return new FirebaseAdapter(
            new HttpClient(handler),
            new FirebaseAdapterOptions(new Uri("https://example.firebaseio.com"), "monitoringArtifacts"));
    }

    private static IMonitoringArtifactRepository CreateInMemoryRepository()
    {
        return new InMemoryMonitoringArtifactRepository();
    }

    private static MonitoringArtifact CreateArtifact(string id, string tenantId)
    {
        return new MonitoringArtifact(
            id,
            tenantId,
            "capture",
            "recording",
            "{\"url\":\"https://example.com/live\"}",
            new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero));
    }

    private sealed class InMemoryMonitoringArtifactRepository : IMonitoringArtifactRepository
    {
        private readonly Dictionary<string, MonitoringArtifact> artifacts = new(StringComparer.Ordinal);

        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            artifacts[artifact.Id] = artifact;
            return Task.CompletedTask;
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            artifacts.TryGetValue(id, out var artifact);
            return Task.FromResult(artifact);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            var tenantArtifacts = artifacts.Values
                .Where(artifact => string.Equals(artifact.TenantId, tenantId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(tenantArtifacts);
        }
    }

    private sealed class InMemoryFirebaseHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, MonitoringArtifact> storage;

        public InMemoryFirebaseHandler(Dictionary<string, MonitoringArtifact> initialStorage)
        {
            storage = initialStorage;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Put)
            {
                var id = ExtractDocumentId(path);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var artifact = JsonSerializer.Deserialize<MonitoringArtifact>(body, SerializerOptions)!;
                storage[id] = artifact;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && path.EndsWith(".json", StringComparison.Ordinal))
            {
                if (path.EndsWith("/monitoringArtifacts.json", StringComparison.Ordinal))
                {
                    var collectionBody = JsonSerializer.Serialize(storage, SerializerOptions);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(collectionBody, Encoding.UTF8, "application/json")
                    };
                }

                var id = ExtractDocumentId(path);
                if (!storage.TryGetValue(id, out var artifact))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var body = JsonSerializer.Serialize(artifact, SerializerOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static string ExtractDocumentId(string absolutePath)
        {
            var fileName = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            return fileName.EndsWith(".json", StringComparison.Ordinal)
                ? fileName[..^5]
                : fileName;
        }
    }
}