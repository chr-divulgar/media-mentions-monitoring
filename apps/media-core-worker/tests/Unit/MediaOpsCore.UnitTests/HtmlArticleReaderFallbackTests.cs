using System.Net;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class HtmlArticleReaderFallbackTests
{
    private const string ArticleHtml = """
        <html><head><meta property="og:title" content="Nota real" /></head>
        <body><article><p>Este es el primer párrafo real de la nota, suficientemente largo para contar como texto.</p></article></body></html>
        """;

    private const string ChallengeHtml = "<html><head><title>Just a moment...</title></head><body>challenge-platform</body></html>";

    private sealed class StubHandler(HttpStatusCode status, string body, params (string Name, string Value)[] headers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeBrowser(string? html) : IBrowserPageReader
    {
        public List<string> Requested { get; } = [];

        public Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            Requested.Add(url);
            return Task.FromResult(html);
        }
    }

    private static HtmlArticleReader Reader(HttpMessageHandler handler, IBrowserPageReader? browser) =>
        new(new HttpClient(handler), 10, NullLogger<HtmlArticleReader>.Instance, browser);

    [Fact]
    public async Task ReadAsync_retries_a_cloudflare_challenge_with_the_browser_and_parses_what_it_returns()
    {
        var browser = new FakeBrowser(ArticleHtml);
        var reader = Reader(new StubHandler(HttpStatusCode.Forbidden, ChallengeHtml, ("cf-mitigated", "challenge"), ("Server", "cloudflare")), browser);

        var content = await reader.ReadAsync("https://cambiocolombia.com/nota");

        Assert.True(content.Succeeded);
        Assert.Equal("Nota real", content.Title);
        Assert.Equal(["https://cambiocolombia.com/nota"], browser.Requested);
    }

    [Fact]
    public async Task ReadAsync_marks_the_article_blocked_when_the_browser_cannot_get_past_the_challenge_either()
    {
        var reader = Reader(new StubHandler(HttpStatusCode.Forbidden, ChallengeHtml, ("cf-mitigated", "challenge")), new FakeBrowser(null));

        var content = await reader.ReadAsync("https://cambiocolombia.com/nota");

        Assert.False(content.Succeeded);
        Assert.True(content.Blocked);
        Assert.Contains("Cloudflare", content.ErrorMessage);
    }

    [Fact]
    public async Task ReadAsync_recognises_the_challenge_page_by_its_content_too()
    {
        var browser = new FakeBrowser(ArticleHtml);
        var reader = Reader(new StubHandler(HttpStatusCode.ServiceUnavailable, ChallengeHtml, ("Server", "cloudflare")), browser);

        var content = await reader.ReadAsync("https://example.com/nota");

        Assert.True(content.Succeeded);
        Assert.Single(browser.Requested);
    }

    [Fact]
    public async Task ReadAsync_does_not_open_a_browser_for_an_ordinary_http_error()
    {
        var browser = new FakeBrowser(ArticleHtml);
        var reader = Reader(new StubHandler(HttpStatusCode.Forbidden, "<html>Forbidden</html>"), browser);

        var content = await reader.ReadAsync("https://example.com/nota");

        Assert.False(content.Succeeded);
        Assert.False(content.Blocked);
        Assert.Contains("403", content.ErrorMessage);
        Assert.Empty(browser.Requested);
    }

    [Fact]
    public async Task ReadAsync_reports_the_challenge_as_blocked_when_no_browser_is_configured()
    {
        var reader = Reader(new StubHandler(HttpStatusCode.Forbidden, ChallengeHtml, ("cf-mitigated", "challenge")), browser: null);

        var content = await reader.ReadAsync("https://example.com/nota");

        Assert.True(content.Blocked);
    }
}
