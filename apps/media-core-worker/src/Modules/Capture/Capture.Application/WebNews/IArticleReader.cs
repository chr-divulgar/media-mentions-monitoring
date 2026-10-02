namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Reads an outlet's own page for the evidence a search result cannot provide: the real publish
/// date (used to verify the article is from today), the title and the first paragraph.
/// </summary>
public interface IArticleReader
{
    Task<ArticleContent> ReadAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record ArticleContent(string? Title, string? FirstParagraph, DateTimeOffset? PublishedAt, string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}
