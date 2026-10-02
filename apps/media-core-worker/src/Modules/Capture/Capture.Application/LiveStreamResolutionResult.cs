namespace MediaOpsCore.Modules.Capture.Application;

public sealed record LiveStreamResolutionResult(
    string? Url,
    LiveStreamResolutionFailure? Failure = null,
    string? Detail = null)
{
    public bool Succeeded => Url is not null;
}

public enum LiveStreamResolutionFailure
{
    Unavailable,
    AuthRequired,
    BinaryNotFound
}
