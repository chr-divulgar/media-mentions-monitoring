using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class HttpStartupSourceDiscoveryService : IStartupSourceDiscoveryService
{
    private static readonly Regex DirectStreamRegex = new(
        "https?://[^\\s\"'<>]+(?:\\.m3u8|\\.aac|\\.mp3|\\.pls|\\.m3u)(?:\\?[^\\s\"'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenericStreamRegex = new(
        "https?://[^\\s\"'<>]+/(?:stream|live)(?:[/?][^\\s\"'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EscapedUrlRegex = new(
        "https?:\\\\/\\\\/[^\\s\"'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SrcOrHrefRegex = new(
        "(?:src|href)\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient httpClient;
    private readonly OperationsWorkerOptions options;

    public HttpStartupSourceDiscoveryService(HttpClient httpClient, OperationsWorkerOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.PrimaryUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(source.PrimaryUrl, UriKind.Absolute, out var primaryUri))
        {
            return null;
        }

        if (LooksLikeStreamUrl(primaryUri.ToString()))
        {
            return primaryUri.ToString();
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(2, options.StartupDiscoveryRequestTimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var html = await httpClient.GetStringAsync(primaryUri, linkedCts.Token).ConfigureAwait(false);
        var candidates = ExtractCandidates(html, primaryUri);

        return candidates.FirstOrDefault();
    }

    private static IEnumerable<string> ExtractCandidates(string html, Uri baseUri)
    {
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateRawCandidates(html))
        {
            if (!Uri.TryCreate(baseUri, candidate, out var resolved))
            {
                continue;
            }

            var resolvedValue = resolved.ToString();
            if (!LooksLikeStreamUrl(resolvedValue))
            {
                continue;
            }

            if (dedup.Add(resolvedValue))
            {
                yield return resolvedValue;
            }
        }
    }

    private static IEnumerable<string> EnumerateRawCandidates(string html)
    {
        foreach (Match match in DirectStreamRegex.Matches(html))
        {
            yield return match.Value;
        }

        foreach (Match match in GenericStreamRegex.Matches(html))
        {
            yield return match.Value;
        }

        foreach (Match match in EscapedUrlRegex.Matches(html))
        {
            yield return match.Value.Replace("\\/", "/", StringComparison.Ordinal);
        }

        foreach (Match match in SrcOrHrefRegex.Matches(html))
        {
            var value = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            yield return value;
        }
    }

    private static bool LooksLikeStreamUrl(string value)
    {
        if (value.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".aac", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".mp3", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".pls", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".m3u", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/stream", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/live", StringComparison.OrdinalIgnoreCase);
    }
}
