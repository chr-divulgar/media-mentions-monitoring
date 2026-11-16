namespace MediaOpsCore.Modules.Capture.Application;

public static class WebArticleStatus
{
    public const string Alerted = "alerted";
    public const string NotToday = "not-today";
    public const string NoDate = "no-date";
    public const string NoKeyword = "no-keyword";
    public const string Failed = "failed";
    // Final, unlike Failed: the outlet blocks automated access (e.g. a Cloudflare challenge), so
    // the retry of failed articles must not pick it up again.
    public const string Blocked = "blocked";
    // The same address as another note of this cycle (found by the News RSS and by the direct search): that one is the
    // note; this record only keeps the RSS item from being resolved again every cycle.
    public const string Duplicate = "duplicate";
}

/// <summary>
/// The keys that tell the same story apart from a new one across sources. The same story often comes with two addresses
/// (Google Alerts: ".../publicacion/53431"; Google News: ".../publicacion/53431-ecopetrol-planea-..."), so besides the
/// address it is also known by its site and headline.
/// </summary>
public static class WebArticleKeys
{
    public static string Url(string url) => $"url:{url}";

    // "story:ciudadccs.info|ecopetrol planea reactivar gasoducto ..."; null when there is no headline to compare.
    public static string? Story(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var headline = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"story:{host.ToLowerInvariant()}|{headline.ToLowerInvariant()}";
    }
}

public sealed record WebArticleRecord(
    string GoogleId,
    string Url,
    string Outlet,
    string? Title,
    string? FirstParagraph,
    DateTimeOffset? PublishedAt,
    string Status,
    IReadOnlyList<string> MatchedKeywords,
    IReadOnlyList<string> Clients,
    DateTimeOffset DiscoveredAt);

public interface IWebArticleRepository
{
    // The cheap check that makes re-running a 24h search every cycle affordable: everything in
    // this set is skipped before any HTTP fetch happens. Besides the Google ids it holds each article's
    // WebArticleKeys.Url and, for those not failed, its WebArticleKeys.Story.
    Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default);

    // True when the article will not be tried again: every status except Failed is final, and a Failed
    // one becomes final once it has used up its attempts. Lets the caller act on the last failure.
    Task<bool> SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default);
}
