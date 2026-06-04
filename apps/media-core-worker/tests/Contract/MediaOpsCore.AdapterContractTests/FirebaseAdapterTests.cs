using System.Net;
using System.Text;
using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.BuildingBlocks.Infrastructure;
using Xunit;

namespace MediaOpsCore.AdapterContractTests;

public sealed class FirebaseAdapterTests
{
    [Fact]
    public async Task UpsertAsync_should_write_the_canonical_artifact_shape_to_firebase()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = CreateAdapter(handler);
        var artifact = CreateArtifact();

        await adapter.UpsertAsync(artifact);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("https://example.firebaseio.com/monitoringArtifacts/artifact-1.json", handler.LastRequest.RequestUri!.AbsoluteUri);

        var body = handler.LastRequestBody;
        Assert.NotNull(body);
        Assert.Contains("\"tenantId\":\"tenant-a\"", body, StringComparison.Ordinal);
        Assert.Contains("\"payloadJson\":", body, StringComparison.Ordinal);
        Assert.Contains("https://example.com/live", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_should_round_trip_the_canonical_artifact_shape()
    {
        var responseBody = JsonSerializer.Serialize(CreateArtifact(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        var adapter = CreateAdapter(handler);

        var artifact = await adapter.GetAsync("artifact-1");

        Assert.NotNull(artifact);
        Assert.Equal("artifact-1", artifact!.Id);
        Assert.Equal("tenant-a", artifact.TenantId);
        Assert.Equal("recording", artifact.Kind);
        Assert.Equal("{\"url\":\"https://example.com/live\"}", artifact.PayloadJson);
    }

    [Fact]
    public async Task ListByTenantAsync_should_filter_by_tenant_on_collection_reads()
    {
        var responseBody = JsonSerializer.Serialize(
            new Dictionary<string, MonitoringArtifact>
            {
                ["artifact-1"] = CreateArtifact(),
                ["artifact-2"] = new MonitoringArtifact(
                    "artifact-2",
                    "tenant-b",
                    "capture",
                    "recording",
                    "{\"url\":\"https://example.org/live\"}",
                    new DateTimeOffset(2026, 6, 3, 12, 30, 0, TimeSpan.Zero))
            },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        var adapter = CreateAdapter(handler);

        var artifacts = await adapter.ListByTenantAsync("tenant-a");

        Assert.Single(artifacts);
        Assert.Equal("artifact-1", artifacts[0].Id);
    }

    private static FirebaseAdapter CreateAdapter(RecordingHandler handler)
    {
        return new FirebaseAdapter(
            new HttpClient(handler),
            new FirebaseAdapterOptions(new Uri("https://example.firebaseio.com"), "monitoringArtifacts"));
    }

    private static MonitoringArtifact CreateArtifact()
    {
        return new MonitoringArtifact(
            "artifact-1",
            "tenant-a",
            "capture",
            "recording",
            "{\"url\":\"https://example.com/live\"}",
            new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public RecordingHandler(HttpResponseMessage response)
        {
            this.response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
    }
}