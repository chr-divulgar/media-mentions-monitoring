namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// HTTP response body for POST /youtube/cookies endpoint.
/// Indicates success or failure of cookie reception.
/// </summary>
public sealed class YouTubeCookiesResponse
{
    /// <summary>
    /// Whether cookies were successfully received and saved.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable message describing the result or error.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
