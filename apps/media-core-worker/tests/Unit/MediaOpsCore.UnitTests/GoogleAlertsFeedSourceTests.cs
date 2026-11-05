using System.Net;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Google Alerts feeds (Atom): alerts search the whole text of news and web pages, so they bring the stories that name
// the client only in their body, which Google News never offers.
public sealed class GoogleAlertsFeedSourceTests
{
    private const string FeedA = "https://www.google.com/alerts/feeds/123/456";
    private const string FeedB = "https://www.google.com/alerts/feeds/123/789";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(-5));

    private const string EcopetrolFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:idx="urn:atom-extension:indexing">
          <id>tag:google.com,2005:reader/user/123/state/com.google/alerts/456</id>
          <title>Alerta de Google: Ecopetrol</title>
          <link href="https://www.google.com/alerts/feeds/123/456" rel="self"></link>
          <updated>2026-11-04T20:10:00Z</updated>
          <entry>
            <id>tag:google.com,2013:googlealerts/feed:111</id>
            <title type="html">Este es el cronograma para reactivar el pol&amp;eacute;mico gasoducto con &lt;b&gt;Ecopetrol&lt;/b&gt;</title>
            <link href="https://www.google.com/url?rct=j&amp;sa=t&amp;url=https://www.portafolio.co/energia/gasoducto-505716&amp;ct=ga&amp;cd=CAIy&amp;usg=AOv"></link>
            <published>2026-11-04T20:10:00Z</published>
            <updated>2026-11-04T20:10:00Z</updated>
            <content type="html">... la estatal &lt;b&gt;Ecopetrol&lt;/b&gt; ...</content>
          </entry>
          <entry>
            <id>tag:google.com,2013:googlealerts/feed:222</id>
            <title type="html">Importar gas de Venezuela tambi&amp;eacute;n est&amp;aacute; en la agenda</title>
            <link href="https://www.google.com/url?rct=j&amp;sa=t&amp;url=https://www.elespectador.com/economia/importar-gas%3Fref%3Dx&amp;ct=ga"></link>
            <published>2026-11-04T15:21:10Z</published>
          </entry>
        </feed>
        """;

    private const string OtherFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <id>tag:google.com,2013:googlealerts/feed:333</id>
            <title type="html">Otra vez el gasoducto</title>
            <link href="https://www.google.com/url?rct=j&amp;sa=t&amp;url=https://www.portafolio.co/energia/gasoducto-505716&amp;ct=ga"></link>
          </entry>
        </feed>
        """;

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Atom(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml") };

    private static (GoogleAlertsFeedSource Source, FakeHandler Handler) Build(
        IReadOnlyList<string> feeds, Func<Uri, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var source = new GoogleAlertsFeedSource(
            new HttpClient(handler), _ => Task.FromResult(feeds), 20, NullLogger<GoogleAlertsFeedSource>.Instance, () => Now);
        return (source, handler);
    }

    [Fact]
    public async Task Reads_the_real_address_a_clean_title_and_the_outlet_of_each_entry()
    {
        var (source, _) = Build([FeedA], _ => Atom(EcopetrolFeed));

        var items = await source.ReadAsync();

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("https://www.portafolio.co/energia/gasoducto-505716", first.GoogleLink); // not Google's redirect
        Assert.Equal("url:https://www.portafolio.co/energia/gasoducto-505716", first.GoogleId);
        Assert.True(first.DirectLink);
        Assert.Equal("Este es el cronograma para reactivar el polémico gasoducto con Ecopetrol", first.Title);
        Assert.Equal("portafolio.co", first.Outlet);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 20, 10, 0, TimeSpan.Zero), first.PublishedAt);
        // The address keeps its own query, decoded once.
        Assert.Equal("https://www.elespectador.com/economia/importar-gas?ref=x", items[1].GoogleLink);
        Assert.Equal("elespectador.com", items[1].Outlet);
    }

    [Fact]
    public async Task The_same_address_from_two_feeds_comes_once()
    {
        var (source, handler) = Build([FeedA, FeedB], uri => Atom(uri.AbsoluteUri == FeedA ? EcopetrolFeed : OtherFeed));

        var items = await source.ReadAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, items.Count);
        Assert.Single(items, item => item.GoogleLink == "https://www.portafolio.co/energia/gasoducto-505716");
    }

    [Fact]
    public async Task A_feed_that_fails_does_not_stop_the_others_and_never_throws()
    {
        var (source, _) = Build([FeedA, FeedB], uri => uri.AbsoluteUri == FeedA
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Atom(OtherFeed));

        var items = await source.ReadAsync();

        Assert.Single(items);

        var broken = new GoogleAlertsFeedSource(
            new HttpClient(new FakeHandler(_ => Atom("<not xml"))), _ => Task.FromResult<IReadOnlyList<string>>([FeedA]), 20,
            NullLogger<GoogleAlertsFeedSource>.Instance);
        Assert.Empty(await broken.ReadAsync());

        var noFeedList = new GoogleAlertsFeedSource(
            new HttpClient(new FakeHandler(_ => Atom(OtherFeed))), _ => throw new InvalidOperationException("mongo down"), 20,
            NullLogger<GoogleAlertsFeedSource>.Instance);
        Assert.Empty(await noFeedList.ReadAsync());
    }

    [Fact]
    public async Task Without_feeds_it_asks_nothing()
    {
        var (source, handler) = Build([], _ => Atom(EcopetrolFeed));

        Assert.Empty(await source.ReadAsync());
        Assert.Empty(handler.Requests);
    }
}
