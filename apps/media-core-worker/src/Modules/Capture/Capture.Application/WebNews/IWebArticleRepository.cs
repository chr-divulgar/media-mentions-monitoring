namespace MediaOpsCore.Modules.Capture.Application;

public static class WebArticleStatus
{
    public const string Alerted = "alerted";
    public const string NotToday = "not-today";
    public const string NoDate = "no-date";
    public const string NoKeyword = "no-keyword";
    public const string Failed = "failed";
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
    // this set is skipped before any HTTP fetch happens.
    Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default);

    Task SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default);
}
