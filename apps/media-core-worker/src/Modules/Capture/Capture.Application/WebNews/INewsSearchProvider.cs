namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Discovers candidate news articles for a search query — used only to FIND stories. The
/// published date, title and body always come from the outlet's own page (see IArticleReader),
/// never from the search result itself: tender rules reject information "sacada de buscadores".
/// </summary>
public interface INewsSearchProvider
{
    Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

// GoogleId: the search provider's own opaque identifier for this result, used to recognize an
// already-seen article across cycles without resolving its real URL first — resolving is the
// expensive step this lets most cycles skip entirely.
public sealed record NewsSearchItem(string GoogleId, string Title, string Outlet, string GoogleLink, DateTimeOffset PublishedAt);
