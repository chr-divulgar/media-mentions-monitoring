using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads an outlet's own page — never Google's search result — for the publish date, title and
/// first paragraph a web-news mention needs. Tries structured metadata first (JSON-LD, OpenGraph),
/// which most news sites publish for their own SEO, before falling back to the raw DOM.
/// </summary>
public sealed partial class HtmlArticleReader : IArticleReader
{
    // Real paragraphs run well past this; shorter text here is reliably a caption, byline or
    // share-bar label rather than the lead paragraph.
    private const int MinFirstParagraphLength = 80;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";
    private static readonly string[] NonBodyTags = ["figcaption", "figure", "aside", "nav", "footer"];

    private readonly HttpClient httpClient;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<HtmlArticleReader> logger;

    public HtmlArticleReader(HttpClient httpClient, int requestTimeoutSeconds, ILogger<HtmlArticleReader> logger)
    {
        this.httpClient = httpClient;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
    }

    public async Task<ArticleContent> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ArticleContent(null, null, null, $"El sitio respondió HTTP {(int)response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            return await ParseAsync(html).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ArticleContent(null, null, null, $"Tiempo agotado leyendo la página ({requestTimeoutSeconds}s)");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[HtmlArticleReader] Failed to read {Url}.", url);
            return new ArticleContent(null, null, null, exception.Message);
        }
    }

    // Internal so the extraction heuristics can be covered with saved HTML fixtures.
    internal static async Task<ArticleContent> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html)).ConfigureAwait(false);

        var title = ExtractTitle(document);
        var publishedAt = ExtractPublishedAt(document);
        var firstParagraph = ExtractFirstParagraph(document) ?? ExtractMetaContent(document, "og:description");

        return new ArticleContent(title, firstParagraph, publishedAt, null);
    }

    private static string? ExtractTitle(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (TryFindJsonLdString(script.TextContent, "headline") is { } headline)
            {
                return headline;
            }
        }

        return ExtractMetaContent(document, "og:title") ?? document.QuerySelector("title")?.TextContent?.Trim();
    }

    private static DateTimeOffset? ExtractPublishedAt(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (TryFindJsonLdString(script.TextContent, "datePublished") is { } raw && TryParseDate(raw, out var parsed))
            {
                return parsed;
            }
        }

        var candidate =
            document.QuerySelector("meta[property='article:published_time']")?.GetAttribute("content") ??
            document.QuerySelector("meta[itemprop='datePublished']")?.GetAttribute("content") ??
            document.QuerySelector("time[datetime]")?.GetAttribute("datetime");

        return TryParseDate(candidate, out var result) ? result : null;
    }

    private static string? ExtractFirstParagraph(IDocument document)
    {
        var scope = document.QuerySelector("article") ?? document.Body;
        if (scope is null)
        {
            return null;
        }

        foreach (var paragraph in scope.QuerySelectorAll("p"))
        {
            if (IsInsideNonBodyElement(paragraph))
            {
                continue;
            }

            var text = CollapseWhitespace(paragraph.TextContent);
            if (text.Length >= MinFirstParagraphLength)
            {
                return text;
            }
        }

        return null;
    }

    private static bool IsInsideNonBodyElement(IElement element)
    {
        for (var current = element.ParentElement; current is not null; current = current.ParentElement)
        {
            if (NonBodyTags.Contains(current.TagName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractMetaContent(IDocument document, string property)
    {
        var content = document.QuerySelector($"meta[property='{property}']")?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    private static bool TryParseDate(string? raw, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string CollapseWhitespace(string text) => WhitespaceRun().Replace(text, " ").Trim();

    // JSON-LD news markup is sometimes a single object, sometimes an array of them, and sometimes
    // wrapped in a top-level "@graph" array — this walks all three shapes looking for one string
    // property, which is all either caller needs.
    private static string? TryFindJsonLdString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindString(document.RootElement, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindString(item, propertyName) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValueKind.Object:
                if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                return element.TryGetProperty("@graph", out var graph) ? FindString(graph, propertyName) : null;

            default:
                return null;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
