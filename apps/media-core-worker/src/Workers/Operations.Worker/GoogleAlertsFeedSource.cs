using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The Google Alerts RSS feeds of the clients (Configuración → Clientes → Google Alerts), read every web-news cycle.
/// Alerts search the whole text of news and web pages, so they bring the stories that name the client only in their
/// body, which Google News (RSS or its News tab) never offers. Only candidates: the date, title and body that get
/// recorded come from the outlet's own page, and the cycle checks all the client's keywords there.
///
/// A feed link is its own credential (anyone with it can read the alert), so it never goes whole to the logs. A feed
/// that fails is logged and skipped; nothing here throws into the cycle.
/// </summary>
public sealed partial class GoogleAlertsFeedSource : INewsFeedSource
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly HttpClient httpClient;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> feedLinks;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<GoogleAlertsFeedSource> logger;
    private readonly Func<DateTimeOffset> clock;

    public GoogleAlertsFeedSource(
        HttpClient httpClient,
        Func<CancellationToken, Task<IReadOnlyList<string>>> feedLinks,
        int requestTimeoutSeconds,
        ILogger<GoogleAlertsFeedSource> logger,
        Func<DateTimeOffset>? clock = null)
    {
        this.httpClient = httpClient;
        this.feedLinks = feedLinks;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<NewsSearchItem>> ReadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> feeds;
        try
        {
            feeds = await feedLinks(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[GoogleAlerts] Could not read the clients' feed links.");
            return [];
        }

        var items = new Dictionary<string, NewsSearchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in feeds.Where(feed => !string.IsNullOrWhiteSpace(feed)).Distinct(StringComparer.Ordinal))
        {
            foreach (var item in await ReadFeedAsync(feed.Trim(), cancellationToken).ConfigureAwait(false))
            {
                items.TryAdd(item.GoogleLink, item);
            }
        }

        return items.Values.ToArray();
    }

    private async Task<IReadOnlyList<NewsSearchItem>> ReadFeedAsync(string feed, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var response = await httpClient.GetAsync(feed, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[GoogleAlerts] Feed …{Feed} answered {Status} (deleted alert or wrong link?).", Tail(feed), (int)response.StatusCode);
                return [];
            }

            var xml = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var items = ParseItems(xml, clock());
            logger.LogInformation("[GoogleAlerts] Feed …{Feed}: {Count} entries.", Tail(feed), items.Count);
            return items;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "[GoogleAlerts] Feed …{Feed} could not be read.", Tail(feed));
            return [];
        }
    }

    internal static IReadOnlyList<NewsSearchItem> ParseItems(string xml, DateTimeOffset now)
    {
        var result = new List<NewsSearchItem>();
        foreach (var entry in XDocument.Parse(xml).Descendants(Atom + "entry"))
        {
            var link = RealAddress(entry.Element(Atom + "link")?.Attribute("href")?.Value);
            var title = CleanTitle(entry.Element(Atom + "title")?.Value);
            if (link is null || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var outlet = link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host;
            var published = DateTimeOffset.TryParse(
                entry.Element(Atom + "published")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : now;
            result.Add(new NewsSearchItem($"url:{link.AbsoluteUri}", title, outlet, link.AbsoluteUri, published, DirectLink: true));
        }

        return result;
    }

    // Google wraps every link in its redirect (https://www.google.com/url?...&url=REAL&...): the outlet's own address
    // is its "url" parameter.
    private static Uri? RealAddress(string? href)
    {
        if (!Uri.TryCreate(href?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/url")
        {
            var real = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
            return Uri.TryCreate(real, UriKind.Absolute, out var target) ? target : null;
        }

        return uri;
    }

    // The title comes as HTML: the alert's words in <b>, and entities (&eacute;).
    private static string CleanTitle(string? title) =>
        WebUtility.HtmlDecode(Tags().Replace(title ?? string.Empty, string.Empty)).Trim();

    // The end of a feed link, enough to tell feeds apart in the log without giving the link away.
    private static string Tail(string feed) => feed.Length <= 6 ? feed : feed[^6..];

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
