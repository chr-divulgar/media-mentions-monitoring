namespace MediaOpsCore.Modules.Capture.Application;

public sealed record LiveStreamResolutionResult(
    string? Url,
    LiveStreamResolutionFailure? Failure = null,
    string? Detail = null,
    // Every URL the resolver produced, when there is more than one (e.g. separate video and audio
    // streams for a YouTube live channel). Url is always the first.
    IReadOnlyList<string>? AllUrls = null)
{
    public bool Succeeded => Url is not null;
}

public enum LiveStreamResolutionFailure
{
    Unavailable,
    AuthRequired,
    BinaryNotFound
}
