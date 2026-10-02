using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class GoogleNewsRssSearchProviderTests
{
    // Trimmed down from a real response for a search of "Ecopetrol" (2026-10-02).
    private const string Fixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>"Ecopetrol" - Google News</title>
            <item>
              <title>Acciones de Ecopetrol lideran el volumen - Valora Analitik</title>
              <link>https://news.google.com/rss/articles/CBMipgFBVV95cUxPRg?oc=5</link>
              <pubDate>Fri, 02 Oct 2026 12:42:08 GMT</pubDate>
              <source url="https://www.valoraanalitik.com">Valora Analitik</source>
            </item>
            <item>
              <title>Durante la última década, las deudas de Ecopetrol crecieron - LaRepublica.co</title>
              <link>https://news.google.com/rss/articles/CBMiwgFBVV95cUxOU0l?oc=5</link>
              <pubDate>Fri, 02 Oct 2026 05:01:33 GMT</pubDate>
              <source url="https://www.larepublica.co">LaRepublica.co</source>
            </item>
            <item>
              <title>Sin enlace de Google News</title>
              <link>https://example.com/not-a-google-news-link</link>
              <pubDate>Fri, 02 Oct 2026 05:01:33 GMT</pubDate>
              <source url="https://example.com">Example</source>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void ParseItems_extracts_the_google_id_outlet_and_date_from_each_item()
    {
        var items = GoogleNewsRssSearchProvider.ParseItems(Fixture);

        Assert.Equal(2, items.Count);
        Assert.Equal("CBMipgFBVV95cUxPRg", items[0].GoogleId);
        Assert.Equal("Valora Analitik", items[0].Outlet);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 42, 8, TimeSpan.Zero), items[0].PublishedAt);
    }

    [Fact]
    public void ParseItems_strips_the_outlet_suffix_google_appends_to_every_title()
    {
        var items = GoogleNewsRssSearchProvider.ParseItems(Fixture);

        Assert.Equal("Acciones de Ecopetrol lideran el volumen", items[0].Title);
        Assert.Equal("Durante la última década, las deudas de Ecopetrol crecieron", items[1].Title);
    }

    [Fact]
    public void ParseItems_skips_items_whose_link_is_not_a_google_news_article_link()
    {
        var items = GoogleNewsRssSearchProvider.ParseItems(Fixture);

        Assert.DoesNotContain(items, item => item.Outlet == "Example");
    }

    [Fact]
    public void ParseItems_returns_nothing_for_a_feed_with_no_items()
    {
        var items = GoogleNewsRssSearchProvider.ParseItems("<rss version=\"2.0\"><channel></channel></rss>");

        Assert.Empty(items);
    }
}
