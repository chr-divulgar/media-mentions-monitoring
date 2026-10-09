namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Reads an outlet's own page for the evidence a search result cannot provide: the real publish
/// date (used to verify the article is from today), the title and the first paragraph.
/// </summary>
public interface IArticleReader
{
    Task<ArticleContent> ReadAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record ArticleContent(
    string? Title,
    string? FirstParagraph,
    DateTimeOffset? PublishedAt,
    string? ErrorMessage,
    // Every qualifying paragraph, not just the lead one — kept only for keyword matching, so a
    // client's keyword that shows up later in the body (not the lead paragraph an alert displays)
    // still counts as a mention. Null when no body text could be identified at all.
    string? FullText = null,
    // The site refused automated access outright (an anti-bot challenge that not even the
    // browser fallback got past) — a retry would hit the same wall, so it is final.
    bool Blocked = false,
    // Null when PublishedAt came from structured/conventional markup. "page-text-scan" when no
    // convention matched and the date was guessed from the first date-like text on the page — a
    // weaker signal, kept so those records can be audited or surfaced later.
    string? PublishedAtSource = null)
{
    public bool Succeeded => ErrorMessage is null;
}
