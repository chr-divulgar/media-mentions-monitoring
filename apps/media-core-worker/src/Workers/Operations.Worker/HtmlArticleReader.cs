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
    // "a" excludes the "related content" / "more from this section" teaser cards every outlet's
    // template wraps in an anchor (title + date as two <p>s inside <a href="...">) — real body
    // prose is never itself link text, so this generically keeps those cards out without having to
    // name every outlet's "related" CSS class. Confirmed against a real page (La Patria's cartoon
    // "related" carousel) whose teaser titles could otherwise pass the length/prose checks below.
    private static readonly string[] NonBodyTags = ["figcaption", "figure", "aside", "nav", "footer", "a"];

    private readonly HttpClient httpClient;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<HtmlArticleReader> logger;
    // Only used for pages behind an anti-bot challenge; null disables the fallback.
    private readonly IBrowserPageReader? browser;

    public HtmlArticleReader(HttpClient httpClient, int requestTimeoutSeconds, ILogger<HtmlArticleReader> logger, IBrowserPageReader? browser = null)
    {
        this.httpClient = httpClient;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
        this.browser = browser;
    }

    public Task<ArticleContent> ReadAsync(string url, CancellationToken cancellationToken = default) =>
        ReadAsync(url, 0, cancellationToken);

    private async Task<ArticleContent> ReadAsync(string url, int redirectHops, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "es-CO,es;q=0.9,en;q=0.8");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);

            var html = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

            if (IsCloudflareChallenge(response, html))
            {
                return await ReadWithBrowserAsync(url, cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ArticleContent(null, null, null, $"El sitio respondió HTTP {(int)response.StatusCode}");
            }

            // Link shorteners (t.co) answer 200 with a tiny page that only redirects from the
            // browser; the real article is one more hop away.
            if (redirectHops < MaxClientRedirects && FindClientRedirect(html, response.RequestMessage?.RequestUri) is { } target)
            {
                logger.LogInformation("[HtmlArticleReader] {Url} is a client-side redirect stub; following to {Target}.", url, target);
                return await ReadAsync(target, redirectHops + 1, cancellationToken).ConfigureAwait(false);
            }

            return LogDateSource(url, await ParseAsync(html).ConfigureAwait(false));
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

    private const int MaxClientRedirects = 2;
    // A real article is far bigger; only a stub this small is trusted to be "just a redirect".
    private const int MaxRedirectStubLength = 4000;

    // The target of a <meta http-equiv="refresh"> or location.replace("...") stub, else null.
    internal static string? FindClientRedirect(string html, Uri? baseUri)
    {
        if (html.Length > MaxRedirectStubLength)
        {
            return null;
        }

        var raw = MetaRefreshTarget().Match(html) is { Success: true } meta
            ? meta.Groups[1].Value
            : LocationReplaceTarget().Match(html) is { Success: true } script ? script.Groups[1].Value.Replace(@"\/", "/") : null;
        if (raw is null)
        {
            return null;
        }

        var target = System.Net.WebUtility.HtmlDecode(raw.Trim());
        return Uri.TryCreate(baseUri, target, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.ToString() : null;
    }

    [GeneratedRegex(@"http-equiv\s*=\s*[""']?refresh[""']?[^>]*content\s*=\s*[""']\s*\d+\s*;\s*url\s*=\s*([^""'>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshTarget();

    [GeneratedRegex(@"location\.(?:replace|href)\s*(?:\(\s*|=\s*)[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LocationReplaceTarget();

    // The page a real browser passes on its own (a script checks the browser and sets a cookie) but
    // a plain HTTP client never can — no point retrying it the plain way.
    private static bool IsCloudflareChallenge(HttpResponseMessage response, string html)
    {
        if (response.StatusCode is not (System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.ServiceUnavailable))
        {
            return false;
        }

        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated)
            && mitigated.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fromCloudflare = response.Headers.Server.ToString().Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
        return fromCloudflare
            && (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ArticleContent> ReadWithBrowserAsync(string url, CancellationToken cancellationToken)
    {
        const string Blocked = "Bloqueado por Cloudflare (anti-bot)";
        if (browser is null)
        {
            return new ArticleContent(null, null, null, Blocked, Blocked: true);
        }

        logger.LogInformation("[HtmlArticleReader] Cloudflare challenge on {Url}; retrying with the headless browser.", url);
        var html = await browser.GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
        return html is null
            ? new ArticleContent(null, null, null, $"{Blocked}, también con navegador", Blocked: true)
            : LogDateSource(url, await ParseAsync(html).ConfigureAwait(false));
    }

    private ArticleContent LogDateSource(string url, ArticleContent content)
    {
        if (content.PublishedAtSource is not null)
        {
            logger.LogWarning(
                "[HtmlArticleReader] No conventional publish date on {Url}; used {Source}: {PublishedAt:O}.",
                url, content.PublishedAtSource, content.PublishedAt);
        }

        return content;
    }

    // Internal so the extraction heuristics can be covered with saved HTML fixtures.
    internal static async Task<ArticleContent> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html)).ConfigureAwait(false);

        var title = ExtractTitle(document);
        var publishedAt = ExtractPublishedAt(document);
        string? publishedAtSource = null;
        if (publishedAt is null && ScanPageTextForDate(document) is { } scanned)
        {
            publishedAt = scanned;
            publishedAtSource = PageTextScanSource;
        }

        var paragraphs = ExtractQualifyingParagraphs(document);
        var metaDescription = paragraphs.Count == 0 ? ExtractMetaContent(document, "og:description") : null;
        var firstParagraph = paragraphs.Count > 0 ? paragraphs[0] : metaDescription;
        // Keyword matching needs every paragraph a client's name could appear in, not just the
        // lead one an alert displays — see ArticleContent.FullText's doc comment.
        var fullText = paragraphs.Count > 0 ? string.Join(" ", paragraphs) : metaDescription;

        // Only the lead sentence is kept for display — the rest adds length without adding to what
        // an alert needs to say. The 80-char "is this a real paragraph" check already ran on the
        // full text, so a paragraph that merely opens with a short sentence is never discarded by
        // this. FullText above is built from the untruncated paragraphs, before this runs.
        if (firstParagraph is not null)
        {
            firstParagraph = TruncateAtFirstSentence(firstParagraph);
        }

        return new ArticleContent(title, firstParagraph, publishedAt, null, fullText, PublishedAtSource: publishedAtSource);
    }

    private const string PageTextScanSource = "page-text-scan";

    // ponytail: last resort — first date-like text in the article (or body), no notion of which
    // date is "the" publish date. Upgrade path: weigh matches by proximity to the title.
    private static DateTimeOffset? ScanPageTextForDate(IDocument document)
    {
        var scope = document.QuerySelector("article") ?? document.Body;
        if (scope is null)
        {
            return null;
        }

        foreach (var node in scope.QuerySelectorAll("*"))
        {
            // Skip script/style content and only look at an element's own text, in document order.
            if (node.LocalName is "script" or "style" or "noscript")
            {
                continue;
            }

            foreach (var child in node.ChildNodes.Where(n => n.NodeType == NodeType.Text))
            {
                foreach (Match match in DateLikeText().Matches(child.TextContent))
                {
                    var text = match.Value;
                    // Exact Spanish formats first: invariant parsing would read "06/10/2026" as June 10.
                    if (DateTime.TryParseExact(text, SpanishDateFormats, Spanish, DateTimeStyles.None, out var exact))
                    {
                        return new DateTimeOffset(exact, BogotaOffset);
                    }

                    if (TryParseFlexibleDate(text, out var flexible))
                    {
                        return flexible;
                    }
                }
            }
        }

        return null;
    }

    // ISO (2026-10-06), numeric day-first (06/10/2026) and Spanish month names ("06 Oct 2026",
    // "6 de octubre de 2026").
    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}[/.-]\d{1,2}[/.-]\d{4}\b|\b\d{1,2}\s+(?:de\s+)?[\p{L}]{3,10}\.?\s+(?:de\s+)?\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateLikeText();

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

    // Tried in order, stopping at the first one that actually parses — not just the first one that
    // exists. A site publishing a value in its first candidate tag that our parser can't read used
    // to hide perfectly good values in every candidate tried after it (see DateCandidates' doc
    // comment history: the old code chained candidates with `??`, which only moves on when a
    // selector finds nothing, not when its text fails to parse).
    private static readonly Func<IDocument, string?>[] DateCandidates =
    [
        d => d.QuerySelector("meta[property='article:published_time']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='article:published_time']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[itemprop='datePublished']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='date']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='pubdate']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='publish-date']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='sailthru.date']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='parsely-pub-date']")?.GetAttribute("content"),
        d => d.QuerySelector("meta[name='DC.date.issued']")?.GetAttribute("content"),
        // Drupal RDFa (seguimiento.co): <span property="dc:date" content="2026-10-05T16:17:00-05:00">.
        d => d.QuerySelector("[property='dc:date'][content]")?.GetAttribute("content"),
        d => d.QuerySelector("time[datetime]")?.GetAttribute("datetime"),
        // Some sites print the date as a <time> tag's visible text with no datetime attribute at all.
        d => d.QuerySelector("time")?.TextContent,
        // Themes with no <time> and no meta tags (e.g. revistametro.co) print it as <span class="date">06 Oct 2026</span>.
        d => d.QuerySelector("span.date")?.TextContent,
    ];

    private static DateTimeOffset? ExtractPublishedAt(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (TryFindJsonLdString(script.TextContent, "datePublished") is { } raw && TryParseFlexibleDate(raw, out var parsed))
            {
                return parsed;
            }
        }

        foreach (var candidate in DateCandidates)
        {
            if (candidate(document) is { } raw && TryParseFlexibleDate(raw, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractQualifyingParagraphs(IDocument document)
    {
        var scope = document.QuerySelector("article") ?? document.Body;
        if (scope is null)
        {
            return [];
        }

        var paragraphs = new List<string>();
        foreach (var paragraph in scope.QuerySelectorAll("p"))
        {
            if (IsInsideNonBodyElement(paragraph))
            {
                continue;
            }

            var text = CollapseWhitespace(paragraph.TextContent);
            if (text.Length >= MinFirstParagraphLength && LooksLikeProse(text))
            {
                paragraphs.Add(text);
            }
        }

        return paragraphs;
    }

    // A long string isn't necessarily a paragraph — image credits, tag lists and share-bar labels
    // can also clear the length check. Real Spanish prose reliably uses a few short function words
    // ("de", "la", "que", ...) that a run of captions or keywords strung together normally doesn't;
    // requiring more than one match filters a single incidental word (e.g. "Foto de archivo").
    private const int MinStopwordMatches = 2;

    private static bool LooksLikeProse(string text) => StopwordRun().Matches(text).Count >= MinStopwordMatches;

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

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CO");
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    // One machine-generated format (ISO-8601 etc.) won't read a human-written Spanish date and vice
    // versa, so this tries progressively looser strategies instead of picking one up front: exact
    // ISO-ish parsing, then .NET's own Spanish-culture date recognition (handles "2 de octubre de
    // 2026", "2 oct. 2026" for free, no format list needed), then a short list of explicit formats
    // as a safety net for the one example that prompted this: "10 oct 2026".
    private static readonly string[] SpanishDateFormats =
    [
        "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
        "d 'de' MMMM 'de' yyyy", "d MMMM yyyy", "d MMM yyyy", "d MMM. yyyy",
    ];

    private static bool TryParseFlexibleDate(string? raw, out DateTimeOffset value)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            value = default;
            return false;
        }

        // Structured sources (JSON-LD, article:published_time, ...) are expected to carry their own
        // explicit offset or "Z" per spec, so DateTimeOffset.TryParse is trusted to read it as-is.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        // Free-text dates ("10 oct 2026", "2 de octubre de 2026") never carry a time zone of their
        // own. Parsed as a plain DateTime — not DateTimeOffset, which for zone-less text silently
        // defaults to whatever zone the HOST MACHINE happens to be running in — then anchored to
        // Bogota explicitly, the same zone the rest of this feature uses (see WebNewsClock.IsToday).
        if (DateTime.TryParse(text, Spanish, DateTimeStyles.None, out var spanishLocal))
        {
            value = new DateTimeOffset(spanishLocal, BogotaOffset);
            return true;
        }

        if (DateTime.TryParseExact(text, SpanishDateFormats, Spanish, DateTimeStyles.None, out var exactLocal))
        {
            value = new DateTimeOffset(exactLocal, BogotaOffset);
            return true;
        }

        return false;
    }

    private static string CollapseWhitespace(string text) => WhitespaceRun().Replace(text, " ").Trim();

    // Only the lead sentence survives — the rest is deliberately dropped to keep alerts short. An
    // abbreviation like "Dr. García" still cuts early; that trade-off was accepted over building an
    // abbreviation list. Text with no sentence-ending punctuation at all is left whole.
    private static string TruncateAtFirstSentence(string text) =>
        FirstSentence().Match(text) is { Success: true } match ? match.Value : text;

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

    [GeneratedRegex(@"^.*?[.!?](?=\s|$)", RegexOptions.Singleline)]
    private static partial Regex FirstSentence();

    [GeneratedRegex(@"\b(de|la|el|en|que|y|los|las|un|una|del|con|por|para)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StopwordRun();
}
