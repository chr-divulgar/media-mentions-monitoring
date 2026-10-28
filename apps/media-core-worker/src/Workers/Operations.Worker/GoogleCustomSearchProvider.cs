using System.Globalization;
using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Google's official search API (Programmable Search / Custom Search JSON API), used for the main keyword of each client
/// next to the News RSS. The RSS searches headlines and extracts, so a story that names the client only in its body never
/// comes up; this searches the text of the pages. Like every search provider it only FINDS articles: the date, title and
/// body that get recorded always come from the outlet's own page. Its results carry the real address, so there is no link
/// to resolve.
///
/// Each call counts as one query of the free 100 a day (up to 10 results per call, however many come back). The provider
/// keeps its own daily cap, and a quota answer from Google switches it off until the next day (Bogota), so nothing is ever
/// charged by surprise. It never throws: a failure here must not stop the RSS search that runs in the same cycle.
/// </summary>
public sealed class GoogleCustomSearchProvider : INewsSearchProvider
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const string Endpoint = "https://www.googleapis.com/customsearch/v1";

    private readonly HttpClient httpClient;
    private readonly string apiKey;
    private readonly string searchEngineId;
    private readonly int maxDailyQueries;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<GoogleCustomSearchProvider> logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private DateOnly day;
    private int queriesToday;
    private bool quotaExhausted;

    public GoogleCustomSearchProvider(
        HttpClient httpClient, string apiKey, string searchEngineId, int maxDailyQueries, int requestTimeoutSeconds,
        ILogger<GoogleCustomSearchProvider> logger, Func<DateTimeOffset>? clock = null)
    {
        this.httpClient = httpClient;
        this.apiKey = apiKey;
        this.searchEngineId = searchEngineId;
        this.maxDailyQueries = maxDailyQueries;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !TryReserveQuery())
        {
            return [];
        }

        var url = $"{Endpoint}?key={Uri.EscapeDataString(apiKey)}&cx={Uri.EscapeDataString(searchEngineId)}&q={Uri.EscapeDataString(query)}" +
                  "&dateRestrict=d1&sort=date&num=10&gl=co&lr=lang_es";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var response = await httpClient.GetAsync(url, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
                {
                    lock (gate)
                    {
                        quotaExhausted = true;
                    }

                    logger.LogWarning("[GoogleCustomSearch] Google answered {Status} (quota or access): the API is paused until tomorrow.", (int)response.StatusCode);
                }
                else
                {
                    logger.LogWarning("[GoogleCustomSearch] Google answered {Status} for the search.", (int)response.StatusCode);
                }

                return [];
            }

            return ParseItems(body, clock());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "[GoogleCustomSearch] The search could not be made.");
            return [];
        }
    }

    // One more query of the day, unless the cap (or a quota answer) says no. The day is the Bogota day.
    private bool TryReserveQuery()
    {
        var today = DateOnly.FromDateTime(clock().ToOffset(BogotaOffset).DateTime);
        lock (gate)
        {
            if (today != day)
            {
                day = today;
                queriesToday = 0;
                quotaExhausted = false;
            }

            if (quotaExhausted || queriesToday >= maxDailyQueries)
            {
                return false;
            }

            queriesToday++;
            return true;
        }
    }

    // Internal so the shape of the answer is covered with a saved example, no network.
    internal static IReadOnlyList<NewsSearchItem> ParseItems(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<NewsSearchItem>();
        foreach (var item in items.EnumerateArray())
        {
            var link = item.TryGetProperty("link", out var linkElement) ? linkElement.GetString()?.Trim() : null;
            var rawTitle = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(rawTitle) || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var outlet = (item.TryGetProperty("displayLink", out var display) ? display.GetString() : null) ?? uri.Host;
            outlet = outlet.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? outlet[4..] : outlet;
            result.Add(new NewsSearchItem($"url:{link}", StripOutletSuffix(rawTitle), outlet, link, PublishedAtOf(item) ?? now, DirectLink: true));
        }

        return result;
    }

    // "Title - Outlet" and "Title | OUTLET": the outlet's own name closes most headlines.
    private static string StripOutletSuffix(string title)
    {
        foreach (var separator in new[] { " - ", " | ", " – " })
        {
            var index = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > 10 && title.Length - index - separator.Length <= 40)
            {
                return title[..index].Trim();
            }
        }

        return title;
    }

    // The publish moment some pages declare in their metadata; only a hint, the date that counts is read from the page.
    private static DateTimeOffset? PublishedAtOf(JsonElement item)
    {
        if (item.TryGetProperty("pagemap", out var pagemap)
            && pagemap.TryGetProperty("metatags", out var metatags)
            && metatags.ValueKind == JsonValueKind.Array
            && metatags.GetArrayLength() > 0
            && metatags[0].TryGetProperty("article:published_time", out var published)
            && DateTimeOffset.TryParse(published.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
