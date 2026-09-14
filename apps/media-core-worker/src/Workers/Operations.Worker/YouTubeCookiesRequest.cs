namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// HTTP request body for POST /youtube/cookies endpoint.
/// Contains cookies in Netscape HTTP Cookie File format.
/// </summary>
public sealed class YouTubeCookiesRequest
{
    /// <summary>
    /// Cookies content in Netscape HTTP Cookie File format.
    /// Multi-line string with one cookie per line, tab-separated fields.
    /// </summary>
    public string? Cookies { get; set; }
}
