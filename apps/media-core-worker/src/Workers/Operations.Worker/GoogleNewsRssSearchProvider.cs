using System.Globalization;
using System.Xml.Linq;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Searches Google News' public RSS feed. This is the only "Google" touchpoint in the web-news
/// flow — it only discovers candidate articles; the date, title and paragraph that actually get
/// recorded always come from the outlet's own page (HtmlArticleReader), never from this feed.
/// </summary>
public sealed class GoogleNewsRssSearchProvider : INewsSearchProvider
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";

    private readonly HttpClient httpClient;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<GoogleNewsRssSearchProvider> logger;

    public GoogleNewsRssSearchProvider(HttpClient httpClient, int requestTimeoutSeconds, ILogger<GoogleNewsRssSearchProvider> logger)
    {
        this.httpClient = httpClient;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=es-419&gl=CO&ceid=CO:es-419";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
        var items = ParseItems(xml);

        logger.LogDebug("[GoogleNewsRss] Query '{Query}' returned {Count} item(s).", query, items.Count);
        return items;
    }

    // Internal (not private) so the exact RSS shape can be covered with a saved fixture in tests
    // without a real network call.
    internal static IReadOnlyList<NewsSearchItem> ParseItems(string xml)
    {
        var document = XDocument.Parse(xml);
        var items = new List<NewsSearchItem>();

        foreach (var item in document.Descendants("item"))
        {
            var link = item.Element("link")?.Value?.Trim();
            var rawTitle = item.Element("title")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(rawTitle))
            {
                continue;
            }

            var googleId = ExtractGoogleId(link);
            if (googleId is null)
            {
                continue;
            }

            var outlet = item.Element("source")?.Value?.Trim();
            var title = StripOutletSuffix(rawTitle, outlet);

            var pubDateRaw = item.Element("pubDate")?.Value;
            var publishedAt = DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            items.Add(new NewsSearchItem(googleId, title, string.IsNullOrWhiteSpace(outlet) ? "Desconocido" : outlet, link, publishedAt));
        }

        return items;
    }

    // Google appends " - {outlet}" to every <title> on top of the dedicated <source> element —
    // strip it so the title matches what the outlet's own page actually shows.
    private static string StripOutletSuffix(string title, string? outlet)
    {
        if (string.IsNullOrWhiteSpace(outlet))
        {
            return title;
        }

        var suffix = $" - {outlet}";
        return title.EndsWith(suffix, StringComparison.Ordinal) ? title[..^suffix.Length] : title;
    }

    private static string? ExtractGoogleId(string link)
    {
        const string marker = "/articles/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var idStart = index + marker.Length;
        var idEnd = link.IndexOfAny(['?', '#'], idStart);
        return idEnd < 0 ? link[idStart..] : link[idStart..idEnd];
    }
}
