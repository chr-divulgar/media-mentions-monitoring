using System.Net;
using System.Text;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FirebaseCaptureSourceRepositoryTests
{
    private static FirebaseCaptureSourceRepository Build(
        HttpMessageHandler handler,
        string baseUrl = "https://test.firebaseio.com",
        string? authToken = null,
        int timeoutSeconds = 10)
    {
        var options = new FirebaseCaptureSourceRepositoryOptions
        {
            BaseUrl = baseUrl,
            PlatformsPath = "platforms",
            AuthToken = authToken,
            RequestTimeoutSeconds = timeoutSeconds
        };
        return new FirebaseCaptureSourceRepository(
            new HttpClient(handler),
            options,
            NullLogger<FirebaseCaptureSourceRepository>.Instance);
    }

    [Fact]
    public async Task ListAllAsync_should_GET_correct_platforms_uri()
    {
        Uri? capturedUri = null;
        var handler = new RecordingHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var sut = Build(handler);
        await sut.ListAllAsync();

        Assert.NotNull(capturedUri);
        Assert.StartsWith("https://test.firebaseio.com/platforms.json", capturedUri!.ToString());
    }

    [Fact]
    public async Task ListAllAsync_should_append_auth_token_as_query_parameter()
    {
        Uri? capturedUri = null;
        var handler = new RecordingHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var sut = Build(handler, authToken: "mysecrettoken");
        await sut.ListAllAsync();

        Assert.Contains("?auth=mysecrettoken", capturedUri!.ToString());
    }

    [Fact]
    public async Task ListAllAsync_should_not_append_auth_when_token_is_null()
    {
        Uri? capturedUri = null;
        var handler = new RecordingHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var sut = Build(handler, authToken: null);
        await sut.ListAllAsync();

        Assert.DoesNotContain("auth=", capturedUri!.ToString());
    }

    [Fact]
    public async Task ListAllAsync_should_map_firebase_dictionary_to_capture_sources()
    {
        const string json = """
        {
          "caracol-radio": {
            "sourceId": "caracol-radio",
            "platform": "CaracolRadio",
            "media": "radio",
            "streamUrl": "https://stream.example.com/radio",
            "primaryUrl": "https://caracol.com/radio",
            "country": "colombia"
          },
          "noticias-caracol": {
            "sourceId": "noticias-caracol",
            "platform": "youtube",
            "media": "television",
            "streamUrl": "https://www.youtube.com/@noticiascaracol/live",
            "excluded": true
          }
        }
        """;
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var sut = Build(handler);
        var result = await sut.ListAllAsync();

        Assert.Equal(2, result.Count);
        var radio = result.Single(s => s.SourceId == "caracol-radio");
        Assert.Equal("CaracolRadio", radio.Platform);
        Assert.Equal("radio", radio.Media);
        Assert.Equal("https://caracol.com/radio", radio.PrimaryUrl);
        Assert.False(radio.IsExcluded);

        var tv = result.Single(s => s.SourceId == "noticias-caracol");
        Assert.True(tv.IsExcluded);
    }

    [Fact]
    public async Task ListAllAsync_should_return_empty_and_not_throw_when_firebase_returns_empty_object()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        var sut = Build(handler);
        var result = await sut.ListAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAllAsync_should_throw_HttpRequestException_on_5xx_response()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var sut = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.ListAllAsync());
    }

    [Fact]
    public async Task ListAllAsync_should_throw_HttpRequestException_on_401_response()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var sut = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.ListAllAsync());
    }

    [Fact]
    public async Task ListAllAsync_should_use_custom_platforms_path_in_uri()
    {
        Uri? capturedUri = null;
        var handler = new RecordingHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var options = new FirebaseCaptureSourceRepositoryOptions
        {
            BaseUrl = "https://test.firebaseio.com",
            PlatformsPath = "config/sources",
            RequestTimeoutSeconds = 10
        };
        var sut = new FirebaseCaptureSourceRepository(
            new HttpClient(handler), options, NullLogger<FirebaseCaptureSourceRepository>.Instance);

        await sut.ListAllAsync();

        Assert.Contains("/config/sources.json", capturedUri!.ToString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => this.handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
