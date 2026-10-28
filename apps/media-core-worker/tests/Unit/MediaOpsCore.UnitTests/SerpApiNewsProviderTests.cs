using System.Net;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// SerpApi's Google News tab: it finds stories that name the client only in their body, which the News RSS
// (headlines and extracts) never offers. Free plan: 250 searches a month, so it asks every few hours.
public sealed class SerpApiNewsProviderTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private const string ThreeResults = """
        {
          "search_metadata": { "status": "Success" },
          "news_results": [
            { "position": 1, "title": "ISA reforma sus estatutos y elige nueva Junta Directiva: estos son los cambios aprobados",
              "link": "https://www.portafolio.co/negocios/empresas/isa-reforma-505303", "source": "Portafolio.co", "date": "hace 57 minutos" },
            { "position": 2, "title": "Asamblea de ISA cambia reglas para elegir presidente",
              "link": "https://forbes.co/2026/10/28/empresas/asamblea-de-isa/", "source": "Forbes Colombia", "date": "hace 3 minutos" },
            { "position": 3, "title": "Sin enlace", "source": "X" }
          ]
        }
        """;

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (SerpApiNewsProvider Provider, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Func<DateTimeOffset>? clock = null)
    {
        var handler = new FakeHandler(respond);
        var provider = new SerpApiNewsProvider(
            new HttpClient(handler), "KEY", TimeSpan.FromHours(3), 20, NullLogger<SerpApiNewsProvider>.Instance,
            clock ?? (() => new DateTimeOffset(2026, 10, 28, 10, 0, 0, Bogota)));
        return (provider, handler);
    }

    [Fact]
    public async Task Asks_the_news_tab_for_the_last_day_and_reads_title_link_and_outlet()
    {
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, ThreeResults));

        var items = await provider.SearchAsync("\"ecopetrol\"");

        var query = System.Web.HttpUtility.ParseQueryString(handler.Requests[0].Query);
        Assert.Equal("KEY", query["api_key"]);
        Assert.Equal("google", query["engine"]);
        Assert.Equal("nws", query["tbm"]);
        Assert.Equal("\"ecopetrol\"", query["q"]);
        Assert.Equal("qdr:d", query["tbs"]);
        Assert.Equal("100", query["num"]);

        Assert.Equal(2, items.Count); // the one without a link is skipped
        Assert.Equal("ISA reforma sus estatutos y elige nueva Junta Directiva: estos son los cambios aprobados", items[0].Title);
        Assert.Equal("Portafolio.co", items[0].Outlet);
        Assert.Equal("https://www.portafolio.co/negocios/empresas/isa-reforma-505303", items[0].GoogleLink);
        Assert.Equal("url:https://www.portafolio.co/negocios/empresas/isa-reforma-505303", items[0].GoogleId);
        Assert.True(items[0].DirectLink); // the real address: nothing to resolve
    }

    [Fact]
    public async Task Inside_the_interval_it_does_not_ask_again()
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, Bogota);
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, ThreeResults), () => now);

        await provider.SearchAsync("\"ecopetrol\"");
        now = now.AddMinutes(165);
        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        Assert.Single(handler.Requests);

        now = now.AddMinutes(15); // three hours after the first
        await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{ \"error\": \"Your account has run out of searches.\" }")]
    [InlineData(HttpStatusCode.Unauthorized, "{ \"error\": \"Invalid API key.\" }")]
    [InlineData(HttpStatusCode.OK, "{ \"error\": \"Your account has run out of searches.\" }")]
    public async Task An_error_answer_pauses_it_until_the_next_day_and_never_throws(HttpStatusCode status, string body)
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, Bogota);
        var (provider, handler) = Build(_ => Json(status, body), () => now);

        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        now = now.AddHours(4);
        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        Assert.Single(handler.Requests);

        now = new DateTimeOffset(2026, 10, 29, 0, 5, 0, Bogota);
        await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task No_results_is_just_empty_and_does_not_pause_it()
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, Bogota);
        var (provider, handler) = Build(
            _ => Json(HttpStatusCode.OK, "{ \"error\": \"Google hasn't returned any results for this query.\" }"), () => now);

        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        now = now.AddHours(3);
        await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Network_failures_are_empty_results_not_exceptions()
    {
        var failing = new SerpApiNewsProvider(
            new HttpClient(new FakeHandler(_ => throw new HttpRequestException("network down"))), "K", TimeSpan.FromHours(3), 20,
            NullLogger<SerpApiNewsProvider>.Instance);

        Assert.Empty(await failing.SearchAsync("\"ecopetrol\""));
    }

    [Fact]
    public async Task Each_term_keeps_its_own_interval()
    {
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, ThreeResults));

        await provider.SearchAsync("\"ecopetrol\"");
        await provider.SearchAsync("\"otro cliente\"");

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Without_a_term_it_asks_nothing()
    {
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, ThreeResults));

        Assert.Empty(await provider.SearchAsync("  "));
        Assert.Empty(handler.Requests);
    }
}
