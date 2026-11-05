namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Candidate articles from feeds that already did the searching (Google Alerts): unlike INewsSearchProvider there is no
/// query, the feed brings what its alert found. Like every source it only FINDS articles: the date, title and body that
/// get recorded always come from the outlet's own page. Its items carry the real address (DirectLink).
/// </summary>
public interface INewsFeedSource
{
    Task<IReadOnlyList<NewsSearchItem>> ReadAsync(CancellationToken cancellationToken = default);
}
