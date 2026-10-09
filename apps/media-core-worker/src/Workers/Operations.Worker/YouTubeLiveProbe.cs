using System.Text.RegularExpressions;

namespace MediaOpsCore.Workers.Operations;

public enum YouTubeLiveStatus
{
    /// <summary>The check was not conclusive (network error, consent page, changed markup).</summary>
    Unknown,
    Live,
    NotLive,
}

/// <summary>
/// Cheap "is this channel live right now?" check: one plain GET of the channel's "/live" page, no
/// cookies and no yt-dlp process. It lets the per-minute recovery skip yt-dlp (and the YouTube
/// requests made with the account's cookies) for channels that are plainly not streaming.
/// </summary>
public interface IYouTubeLiveProbe
{
    Task<YouTubeLiveStatus> CheckAsync(string liveUrl, CancellationToken cancellationToken);
}

public sealed partial class YouTubeLiveProbe(HttpClient httpClient) : IYouTubeLiveProbe
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<YouTubeLiveStatus> CheckAsync(string liveUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            // Uri escapes non-ASCII handles (e.g. @PrimeraPáginaColombia) on its own.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(liveUrl));
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "es-CO,es;q=0.9");
            request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");

            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return YouTubeLiveStatus.Unknown;
            }

            return Classify(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException
                                          || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return YouTubeLiveStatus.Unknown;
        }
    }

    /// <summary>
    /// A live channel's "/live" page redirects to the broadcast, so its canonical link is
    /// "/watch?v=…" and the page carries "isLive":true; a channel that is not live keeps a
    /// "/channel/…" (or "/@…") canonical. "isLiveNow" alone is not reliable.
    /// </summary>
    internal static YouTubeLiveStatus Classify(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return YouTubeLiveStatus.Unknown;
        }

        var canonical = CanonicalPattern().Match(html);
        if (!canonical.Success)
        {
            return YouTubeLiveStatus.Unknown;
        }

        // A scheduled broadcast also redirects /live to /watch?v= and carries "isLive":true, but it
        // has not started: the video's own playability status says so. (Only that field: other
        // markers such as "isUpcoming" can also come from unrelated videos on a live page.)
        if (html.Contains("\"playabilityStatus\":{\"status\":\"LIVE_STREAM_OFFLINE\"", StringComparison.Ordinal))
        {
            return YouTubeLiveStatus.NotLive;
        }

        var href = canonical.Groups[1].Value;
        if (href.Contains("/watch?v=", StringComparison.Ordinal) || html.Contains("\"isLive\":true", StringComparison.Ordinal))
        {
            return YouTubeLiveStatus.Live;
        }

        return href.Contains("/channel/", StringComparison.Ordinal) || href.Contains("/@", StringComparison.Ordinal)
            ? YouTubeLiveStatus.NotLive
            : YouTubeLiveStatus.Unknown;
    }

    [GeneratedRegex("rel=\"canonical\" href=\"([^\"]*)\"")]
    private static partial Regex CanonicalPattern();
}
