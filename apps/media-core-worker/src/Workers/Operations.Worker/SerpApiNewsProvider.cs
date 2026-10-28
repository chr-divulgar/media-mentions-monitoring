using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Google's News tab through SerpApi, used for the main keyword of each client next to the News RSS. The RSS searches
/// headlines and extracts, so a story that names the client only in its body never comes up; the News tab finds it.
/// Like every search provider it only FINDS articles: the date, title and body that get recorded always come from the
/// outlet's own page. Its results carry the real address, so there is no link to resolve.
///
/// Every call is one search of the plan (the free one has 250 a month), so each term is asked at most once per interval
/// and for the whole last day, which leaves no gaps between calls (what was seen is skipped by the cycle). An error
/// answer (searches run out, bad key) pauses it until the next day (Bogota). It never throws: a failure here must not
/// stop the RSS search that runs in the same cycle.
/// </summary>
public sealed class SerpApiNewsProvider : INewsSearchProvider
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const string Endpoint = "https://serpapi.com/search.json";

    private readonly HttpClient httpClient;
    private readonly string apiKey;
    private readonly TimeSpan interval;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<SerpApiNewsProvider> logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastAsked = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly? pausedOn;

    public SerpApiNewsProvider(
        HttpClient httpClient, string apiKey, TimeSpan interval, int requestTimeoutSeconds,
        ILogger<SerpApiNewsProvider> logger, Func<DateTimeOffset>? clock = null)
    {
        this.httpClient = httpClient;
        this.apiKey = apiKey;
        this.interval = interval;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !TryReserve(query))
        {
            return [];
        }

        var url = $"{Endpoint}?engine=google&tbm=nws&q={Uri.EscapeDataString(query)}&tbs=qdr:d&gl=co&hl=es" +
                  $"&google_domain=google.com.co&num=100&api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var response = await httpClient.GetAsync(url, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            var error = ErrorOf(body);
            // An empty search comes back as an "error" too; it is just no news.
            if (error is not null && error.Contains("hasn't returned any results", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (!response.IsSuccessStatusCode || error is not null)
            {
                lock (gate)
                {
                    pausedOn = Today();
                }

                logger.LogWarning("[SerpApi] Answered {Status} ({Error}): paused until tomorrow.", (int)response.StatusCode, error);
                return [];
            }

            var items = ParseItems(body, clock());
            logger.LogInformation("[SerpApi] {Count} news for {Query}.", items.Count, query);
            return items;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "[SerpApi] The search could not be made.");
            return [];
        }
    }

    // This term may be asked now: not paused today and its interval since the last call has passed.
    private bool TryReserve(string query)
    {
        var now = clock();
        lock (gate)
        {
            if (pausedOn == Today())
            {
                return false;
            }

            if (lastAsked.TryGetValue(query, out var last) && now - last < interval)
            {
                return false;
            }

            lastAsked[query] = now;
            return true;
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock().ToOffset(BogotaOffset).DateTime);

    private static string? ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return "not JSON";
        }
    }

    private static IReadOnlyList<NewsSearchItem> ParseItems(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("news_results", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<NewsSearchItem>();
        foreach (var item in items.EnumerateArray())
        {
            var link = item.TryGetProperty("link", out var linkElement) ? linkElement.GetString()?.Trim() : null;
            var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var outlet = (item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null) ?? uri.Host;
            // "hace 1 hora" is not a date; the moment it was found stands in, the real date is read from the page.
            result.Add(new NewsSearchItem($"url:{link}", title, outlet, link, now, DirectLink: true));
        }

        return result;
    }
}
