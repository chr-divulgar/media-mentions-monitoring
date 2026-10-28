using System.Net;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Google's official search API (Programmable Search): it searches the text of the pages, so it finds a story that names
// the client only in its body — which the News RSS, searching headlines and extracts, never offers.
public sealed class GoogleCustomSearchProviderTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private const string TwoResults = """
        {
          "items": [
            { "title": "La rosca de De la Espriella y los Gómez en empresas del Estado - La Silla Vacía",
              "link": "https://www.lasillavacia.com/silla-nacional/la-rosca/", "displayLink": "www.lasillavacia.com",
              "snippet": "…", "pagemap": { "metatags": [ { "article:published_time": "2026-10-26T12:19:33-05:00" } ] } },
            { "title": "Exfuncionarios de Petro habrían declarado | DIARIO DEL SUR",
              "link": "https://diariodelsur.com.co/noticias/exfuncionarios", "displayLink": "diariodelsur.com.co" }
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

    private static (GoogleCustomSearchProvider Provider, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, int maxDaily = 100, Func<DateTimeOffset>? clock = null)
    {
        var handler = new FakeHandler(respond);
        var provider = new GoogleCustomSearchProvider(
            new HttpClient(handler), "KEY", "CX", maxDaily, 20, NullLogger<GoogleCustomSearchProvider>.Instance,
            clock ?? (() => new DateTimeOffset(2026, 10, 27, 10, 0, 0, Bogota)));
        return (provider, handler);
    }

    [Fact]
    public async Task Asks_for_the_term_in_the_last_day_by_date_and_reads_title_link_outlet_and_date()
    {
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, TwoResults));

        var items = await provider.SearchAsync("\"ecopetrol\"");

        var query = System.Web.HttpUtility.ParseQueryString(handler.Requests[0].Query);
        Assert.Equal("KEY", query["key"]);
        Assert.Equal("CX", query["cx"]);
        Assert.Equal("\"ecopetrol\"", query["q"]);
        Assert.Equal("d1", query["dateRestrict"]);
        Assert.Equal("date", query["sort"]);
        Assert.Equal("10", query["num"]);

        Assert.Equal(2, items.Count);
        Assert.Equal("La rosca de De la Espriella y los Gómez en empresas del Estado", items[0].Title); // without the outlet suffix
        Assert.Equal("lasillavacia.com", items[0].Outlet);
        Assert.Equal("https://www.lasillavacia.com/silla-nacional/la-rosca/", items[0].GoogleLink);
        Assert.True(items[0].DirectLink); // it is the real address: nothing to resolve
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 19, 33, Bogota), items[0].PublishedAt);
        Assert.Equal("url:https://www.lasillavacia.com/silla-nacional/la-rosca/", items[0].GoogleId);
        // Without a date in the result, the moment it was found stands in: the real date is read from the page.
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 10, 0, 0, Bogota), items[1].PublishedAt);
    }

    [Fact]
    public async Task An_answer_with_no_results_is_just_empty()
    {
        var (provider, _) = Build(_ => Json(HttpStatusCode.OK, "{ \"searchInformation\": { \"totalResults\": \"0\" } }"));

        Assert.Empty(await provider.SearchAsync("\"nada\""));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_quota_answer_stops_the_api_for_the_rest_of_the_day_and_never_throws(HttpStatusCode status)
    {
        var now = new DateTimeOffset(2026, 10, 27, 10, 0, 0, Bogota);
        var (provider, handler) = Build(
            _ => Json(status, "{ \"error\": { \"code\": 429, \"errors\": [ { \"reason\": \"dailyLimitExceeded\" } ] } }"), clock: () => now);

        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));
        Assert.Single(handler.Requests); // the second did not even ask

        now = new DateTimeOffset(2026, 10, 28, 0, 5, 0, Bogota); // the next day in Bogota
        await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Other_failures_are_empty_results_not_exceptions()
    {
        var (provider, _) = Build(_ => Json(HttpStatusCode.InternalServerError, "oops"));
        Assert.Empty(await provider.SearchAsync("\"ecopetrol\""));

        var failing = new GoogleCustomSearchProvider(
            new HttpClient(new FakeHandler(_ => throw new HttpRequestException("network down"))), "K", "C", 100, 20,
            NullLogger<GoogleCustomSearchProvider>.Instance);
        Assert.Empty(await failing.SearchAsync("\"ecopetrol\""));
    }

    [Fact]
    public async Task The_daily_cap_is_kept_and_starts_over_at_midnight_in_bogota()
    {
        var now = new DateTimeOffset(2026, 10, 27, 23, 50, 0, Bogota);
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, TwoResults), maxDaily: 3, clock: () => now);

        for (var i = 0; i < 5; i++) await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(3, handler.Requests.Count); // the cap: no charge by surprise

        now = new DateTimeOffset(2026, 10, 28, 0, 1, 0, Bogota);
        await provider.SearchAsync("\"ecopetrol\"");
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Without_a_term_it_asks_nothing()
    {
        var (provider, handler) = Build(_ => Json(HttpStatusCode.OK, TwoResults));

        Assert.Empty(await provider.SearchAsync("  "));
        Assert.Empty(handler.Requests);
    }
}
