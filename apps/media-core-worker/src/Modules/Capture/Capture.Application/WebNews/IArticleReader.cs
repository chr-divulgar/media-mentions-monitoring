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
    string? FullText = null)
{
    public bool Succeeded => ErrorMessage is null;
}
