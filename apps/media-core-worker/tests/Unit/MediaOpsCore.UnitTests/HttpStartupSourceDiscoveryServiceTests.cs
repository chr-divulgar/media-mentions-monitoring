using System.Net;
using System.Net.Http;
using System.Threading;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class HttpStartupSourceDiscoveryServiceTests
{
    [Fact]
    public async Task TryResolveStreamUrlAsync_should_extract_stream_attribute_for_zeno_host()
    {
        var html = """
            <html><body>
                <button class="station_play" stream="https://stream.zeno.fm/t8sz23cfhfhvv"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "colombia-stereo-maicao-live",
            tenantId: "default",
            platform: "ColombiaStereoMaicao",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://onlineradiobox.com/co/maicaostereo/?cs=co.maicaostereo&played=1");

        var resolved = await sut.TryResolveStreamUrlAsync(source);

        Assert.NotNull(resolved);
        Assert.StartsWith("https://stream.zeno.fm/t8sz23cfhfhvv", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_should_extract_stream_dash_host_with_token()
    {
        var html = """
            <html><body>
                <button stream="https://stream-177.zeno.fm/t8sz23cfhfhvv?zt=abc"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "x",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://example.com");

        var resolved = await sut.TryResolveStreamUrlAsync(source);

        Assert.Equal("https://stream-177.zeno.fm/t8sz23cfhfhvv?zt=abc", resolved);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_should_extract_listen_radio_candidate()
    {
        var html = """
            <html><body>
                <button data-stream="https://djp.sytes.net/listen/iguaraya_stereo/radio"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "iguaraya",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://onlineradiobox.com/co/iguarayastereo/?cs=co.iguarayastereo&played=1");

        var resolved = await sut.TryResolveStreamUrlAsync(source);

        Assert.Equal("https://djp.sytes.net/listen/iguaraya_stereo/radio", resolved);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_should_extract_listen2myradio_candidate()
    {
        var html = """
            <html><body>
                <button stream="http://uk14freenew.listen2myradio.com:37793/;"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "makuira",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://onlineradiobox.com/co/makuira/?cs=co.makuira&played=1");

        var resolved = await sut.TryResolveStreamUrlAsync(source);

        Assert.Equal("http://uk14freenew.listen2myradio.com:37793/;", resolved);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_should_extract_candidate_from_secondary_player_resource_when_primary_has_none()
    {
        var primaryUrl = "https://example.com/al-aire";
        var playerUrl = "https://example.com/player";
        var primaryHtml = """
            <html><body>
                <iframe src="https://example.com/player"></iframe>
            </body></html>
            """;
        var playerHtml = """
            <html><body>
                <button stream="https://stream.zeno.fm/t8sz23cfhfhvv"></button>
            </body></html>
            """;

        using var client = new HttpClient(new RoutedStubHttpMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryUrl] = primaryHtml,
            [playerUrl] = playerHtml
        }));

        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "ecos",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: primaryUrl);

        var resolved = await sut.TryResolveStreamUrlAsync(source);

        Assert.NotNull(resolved);
        Assert.StartsWith("https://stream.zeno.fm/t8sz23cfhfhvv", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverStreamUrlsAsync_should_include_protocol_and_queryless_variants_for_discovered_candidate()
    {
        var html = """
            <html><body>
                <button stream="https://stream-179.zeno.fm/db12gu5a0tzuv?zt=abc"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "frontera",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://example.com/al-aire");

        var candidates = await sut.DiscoverStreamUrlsAsync(source);

        Assert.Contains("https://stream-179.zeno.fm/db12gu5a0tzuv?zt=abc", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://stream-179.zeno.fm/db12gu5a0tzuv", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("http://stream-179.zeno.fm/db12gu5a0tzuv", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://stream.zeno.fm/db12gu5a0tzuv", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverStreamUrlsAsync_should_preserve_non_default_port_when_generating_alternate_scheme_variants()
    {
        var html = """
            <html><body>
                <button stream="https://mediacp16.rootservers.co:8002/stream"></button>
            </body></html>
            """;

        using var client = new HttpClient(new StubHttpMessageHandler(html));
        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "ecos",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: "https://example.com/al-aire");

        var candidates = await sut.DiscoverStreamUrlsAsync(source);

        Assert.Contains("https://mediacp16.rootservers.co:8002/stream", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("http://mediacp16.rootservers.co:8002/stream", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://mediacp16.rootservers.co/stream", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverStreamUrlsAsync_should_resolve_playlist_targets_from_listen_endpoint()
    {
        var primaryUrl = "https://example.com/al-aire";
        var listenUrl = "https://djp.sytes.net/listen/iguaraya_stereo/radio";
        var primaryHtml = """
            <html><body>
                <button data-stream="https://djp.sytes.net/listen/iguaraya_stereo/radio"></button>
            </body></html>
            """;
        var playlist = """
            [playlist]
            NumberOfEntries=1
            File1=http://djp.sytes.net:8000/stream
            """;

        using var client = new HttpClient(new RoutedStubHttpMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryUrl] = primaryHtml,
            [listenUrl] = playlist
        }));

        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "iguaraya",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: primaryUrl);

        var candidates = await sut.DiscoverStreamUrlsAsync(source);

        Assert.Contains("https://djp.sytes.net/listen/iguaraya_stereo/radio", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("http://djp.sytes.net/listen/iguaraya_stereo/radio", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("http://djp.sytes.net:8000/stream", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverStreamUrlsAsync_should_resolve_stream_from_audio_player_config_endpoint()
    {
        var primaryUrl = "https://www.ecosdelcombeima.com/al-aire";
        var audioPlayerUrl = "https://mediacp16.rootservers.co:2020/AudioPlayer/8002?mount=&";
        var playerConfigUrl = "https://mediacp16.rootservers.co:2020/AudioPlayer/8002/playerConfig";

        var primaryHtml = """
            <html><body>
                <iframe src="https://mediacp16.rootservers.co:2020/AudioPlayer/8002?mount=&"></iframe>
            </body></html>
            """;

        var audioPlayerHtml = """
            <html><head>
                <meta name="appUrl" content="https://mediacp16.rootservers.co:2020/AudioPlayer/8002" />
            </head><body></body></html>
            """;

        var playerConfigJson = """
            {
              "type": "icecast",
              "streamAddress": "https://mediacp16.rootservers.co:8002",
              "defaultMountUrl": "stream",
              "mountPoints": ["stream"],
              "generalLinks": [
                { "Link": "https://mediacp16.rootservers.co:2020/tunein/8002/stream/pls" }
              ]
            }
            """;

        using var client = new HttpClient(new RoutedStubHttpMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryUrl] = primaryHtml,
            [audioPlayerUrl] = audioPlayerHtml,
            [playerConfigUrl] = playerConfigJson
        }));

        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "ecos-del-combeima",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: primaryUrl);

        var candidates = await sut.DiscoverStreamUrlsAsync(source);

        Assert.Contains("https://mediacp16.rootservers.co:8002/stream", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverStreamUrlsAsync_should_check_all_secondary_candidates_not_only_first_batch()
    {
        var primaryUrl = "https://example.com/al-aire";
        var primaryHtml = """
            <html><body>
                <iframe src="https://example.com/a"></iframe>
                <iframe src="https://example.com/b"></iframe>
                <iframe src="https://example.com/c"></iframe>
                <iframe src="https://example.com/d"></iframe>
                <iframe src="https://example.com/e"></iframe>
                <iframe src="https://example.com/player-late"></iframe>
            </body></html>
            """;

        var playerHtml = """
            <html><body>
                <button stream="https://stream.zeno.fm/latecandidate"></button>
            </body></html>
            """;

        using var client = new HttpClient(new RoutedStubHttpMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryUrl] = primaryHtml,
            ["https://example.com/a"] = "<html></html>",
            ["https://example.com/b"] = "<html></html>",
            ["https://example.com/c"] = "<html></html>",
            ["https://example.com/d"] = "<html></html>",
            ["https://example.com/e"] = "<html></html>",
            ["https://example.com/player-late"] = playerHtml
        }));

        var options = new OperationsWorkerOptions
        {
            StartupDiscoveryRequestTimeoutSeconds = 5
        };

        var sut = new HttpStartupSourceDiscoveryService(client, options);
        var source = new CaptureSource(
            sourceId: "late-secondary",
            tenantId: "default",
            platform: "x",
            media: "radio",
            streamUrl: "https://bad.example.com/live.aac",
            primaryUrl: primaryUrl);

        var candidates = await sut.DiscoverStreamUrlsAsync(source);

        Assert.Contains("https://stream.zeno.fm/latecandidate", candidates, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string html;

        public StubHttpMessageHandler(string html)
        {
            this.html = html;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            };

            return Task.FromResult(response);
        }
    }

    private sealed class RoutedStubHttpMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> routes;

        public RoutedStubHttpMessageHandler(IReadOnlyDictionary<string, string> routes)
        {
            this.routes = routes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.AbsoluteUri;
            if (routes.TryGetValue(key, out var html))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found")
            });
        }
    }
}
