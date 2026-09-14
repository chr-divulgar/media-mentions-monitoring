namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// HTTP response body for GET /youtube/health. Receiving any response at all (regardless of
/// its content) is itself the liveness signal the caller is checking for.
/// </summary>
public sealed class YouTubeHealthResponse
{
    public bool AuthAlertActive { get; set; }

    public bool CookiesFileExists { get; set; }

    public bool CookiesValid { get; set; }

    public int? CookieCount { get; set; }

    public DateTime? EarliestExpiration { get; set; }

    public bool HasYouTubeDomain { get; set; }

    public IReadOnlyList<string> ExcludedSourceIds { get; set; } = [];

    public int TotalTvSources { get; set; }

    public int ActiveTvSources { get; set; }

    public string Message { get; set; } = string.Empty;
}
