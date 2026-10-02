namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Resolves a search provider's opaque link to the real outlet URL. A separate step because the
/// search result's own link is never an acceptable record of where a mention came from.
/// </summary>
public interface INewsLinkResolver
{
    Task<NewsLinkResolution> ResolveAsync(NewsSearchItem item, CancellationToken cancellationToken = default);
}

public sealed record NewsLinkResolution(string? Url, string? ErrorMessage)
{
    public bool Succeeded => Url is not null;
}
