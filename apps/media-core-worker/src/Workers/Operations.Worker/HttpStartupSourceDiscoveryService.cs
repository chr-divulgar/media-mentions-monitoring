using System.Text.Json;
using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class HttpStartupSourceDiscoveryService : IStartupSourceDiscoveryService
{
    private const int MaxConcurrentSecondaryFetches = 8;

    private static readonly Regex ZenoStreamRegex = new(
        "https?://stream(?:-[0-9]+)?\\.zeno\\.fm/[^\\s\"'<>]+(?:\\?[^\\s\"'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectStreamRegex = new(
        "https?://[^\\s\"'<>]+(?:\\.m3u8|\\.aac|\\.mp3|\\.pls|\\.m3u)(?:\\?[^\\s\"'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenericStreamRegex = new(
        "https?://[^\\s\"'<>]+/(?:stream|live|listen)(?:[/?][^\\s\"'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EscapedUrlRegex = new(
        "https?:\\\\/\\\\/[^\\s\"'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SrcOrHrefRegex = new(
        "(?:src|href)\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StreamAttributeRegex = new(
        "(?:stream|data-stream)\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaAppUrlRegex = new(
        "<meta[^>]*name\\s*=\\s*[\"']appUrl[\"'][^>]*content\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Full Triton streamtheworld live-redirect URL with station callsign already present.
    // Catches the stable redirect form: /api/livestream-redirect/CALLSIGN.mp3|aac|m3u8
    // DirectStreamRegex also catches these (they end in .mp3/.aac), but this regex is
    // explicit and anchored to the known Triton host pattern.
    private static readonly Regex TritonRedirectUrlRegex = new(
        "https?://(?:[a-z0-9-]+\\.)?(?:playerservices|eu-playerservices)\\.streamtheworld\\.com/api/livestream-redirect/[A-Z][A-Z0-9_-]{2,20}\\.(?:mp3|aac|m3u8)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Captures the base Triton redirect URL from a "livestreamMountURL" JSON key.
    private static readonly Regex TritonMountUrlJsonRegex = new(
        "\"livestreamMountURL\"\\s*:\\s*\"(https?://[^\"]+/livestream-redirect)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Captures a Triton station callsign from known JSON key names.
    // Callsigns are uppercase alphanumeric with optional underscore/hyphen, 3-20 chars.
    private static readonly Regex TritonCallsignJsonRegex = new(
        "\"(?:station|callSign|tritonStation|mountPoint|stationMount|tritonCallsign)[A-Za-z0-9]*\"\\s*:\\s*\"([A-Z][A-Z0-9_-]{2,19})\"",
        RegexOptions.Compiled);

    private readonly HttpClient httpClient;
    private readonly OperationsWorkerOptions options;

    public HttpStartupSourceDiscoveryService(HttpClient httpClient, OperationsWorkerOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<IReadOnlyList<string>> DiscoverStreamUrlsAsync(CaptureSource source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.PrimaryUrl))
        {
            return [];
        }

        if (!Uri.TryCreate(source.PrimaryUrl, UriKind.Absolute, out var primaryUri))
        {
            return [];
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(2, options.StartupDiscoveryRequestTimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var html = await httpClient.GetStringAsync(primaryUri, linkedCts.Token).ConfigureAwait(false);

        var primaryCandidates = ExtractCandidates(html, primaryUri).ToList();
        if (primaryCandidates.Count > 0)
        {
            return await ExpandCandidatesAsync(primaryCandidates, linkedCts.Token).ConfigureAwait(false);
        }

        var primaryPlayerConfigCandidates = await DiscoverFromPlayerConfigAsync(html, primaryUri, linkedCts.Token).ConfigureAwait(false);
        if (primaryPlayerConfigCandidates.Count > 0)
        {
            return await ExpandCandidatesAsync(primaryPlayerConfigCandidates, linkedCts.Token).ConfigureAwait(false);
        }

        var secondaryCandidates = await DiscoverFromSecondaryResourcesAsync(html, primaryUri, linkedCts.Token).ConfigureAwait(false);
        return await ExpandCandidatesAsync(secondaryCandidates, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default)
    {
        var candidates = await DiscoverStreamUrlsAsync(source, cancellationToken).ConfigureAwait(false);
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
        // Triton full redirect URLs (with callsign) are checked first — they are unambiguous.
        foreach (var candidate in ExtractTritonCandidatesFromHtml(html))
        {
            yield return candidate;
        }

        foreach (Match match in ZenoStreamRegex.Matches(html))
        {
            yield return match.Value;
        }

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

        foreach (Match match in StreamAttributeRegex.Matches(html))
        {
            var value = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            yield return value;
        }
    }

    // Two strategies for Triton streamtheworld stations:
    //
    // 1. Full redirect URL already present in the source (HTML or JS):
    //    e.g. "https://playerservices.streamtheworld.com/api/livestream-redirect/CR_BARRANQ.mp3"
    //    Caught by TritonRedirectUrlRegex and also by DirectStreamRegex (ends in .mp3).
    //    Both are kept so they surface early before other candidates.
    //
    // 2. Reconstructed from JSON pair ("livestreamMountURL" base URL + nearby callsign key):
    //    Some pages embed the base redirect URL ("livestreamMountURL") and the station callsign
    //    in the same JSON configuration blob. When both are present within a 2000-char window,
    //    the full redirect URL is reconstructed.
    //    This does NOT help when the callsign is assembled dynamically by JS at runtime, but
    //    does help for stations that embed both keys in their server-rendered HTML/JSON config.
    private static IEnumerable<string> ExtractTritonCandidatesFromHtml(string html)
    {
        // Strategy 1: full URL already present
        foreach (Match m in TritonRedirectUrlRegex.Matches(html))
        {
            yield return m.Value;
        }

        // Strategy 2: reconstruct from "livestreamMountURL" + nearby callsign key
        foreach (Match mountMatch in TritonMountUrlJsonRegex.Matches(html))
        {
            var mountBase = mountMatch.Groups[1].Value.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(mountBase))
            {
                continue;
            }

            // Search within ±1000 chars of the mount URL key for a callsign key.
            var windowStart = Math.Max(0, mountMatch.Index - 1000);
            var windowEnd = Math.Min(html.Length, mountMatch.Index + mountMatch.Length + 1000);
            var window = html[windowStart..windowEnd];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match callsignMatch in TritonCallsignJsonRegex.Matches(window))
            {
                var callsign = callsignMatch.Groups[1].Value;
                if (!seen.Add(callsign))
                {
                    continue;
                }

                // mp3 is the most widely supported; aac is common on mobile/HLS stacks.
                yield return $"{mountBase}/{callsign}.mp3";
                yield return $"{mountBase}/{callsign}.aac";
            }
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverFromSecondaryResourcesAsync(string primaryHtml, Uri primaryUri, CancellationToken cancellationToken)
    {
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var secondaryUris = EnumerateSecondaryResources(primaryHtml, primaryUri)
            .ToList();

        using var semaphore = new SemaphoreSlim(MaxConcurrentSecondaryFetches);
        var tasks = secondaryUris
            .Select(uri => ProcessSecondaryResourceAsync(uri, cancellationToken, semaphore))
            .ToList();

        var resolvedCandidates = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var batch in resolvedCandidates)
        {
            foreach (var candidate in batch)
            {
                dedup.Add(candidate);
            }
        }

        return dedup.ToList();
    }

    private async Task<IReadOnlyList<string>> ProcessSecondaryResourceAsync(Uri secondaryUri, CancellationToken cancellationToken, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string secondaryContent;
            try
            {
                secondaryContent = await httpClient.GetStringAsync(secondaryUri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return [];
            }

            foreach (var candidate in ExtractCandidates(secondaryContent, secondaryUri))
            {
                dedup.Add(candidate);
            }

            var playerConfigCandidates = await DiscoverFromPlayerConfigAsync(secondaryContent, secondaryUri, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in playerConfigCandidates)
            {
                dedup.Add(candidate);
            }

            return dedup.ToList();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverFromPlayerConfigAsync(string html, Uri baseUri, CancellationToken cancellationToken)
    {
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MetaAppUrlRegex.Matches(html))
        {
            var appUrlRaw = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(appUrlRaw))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, appUrlRaw, out var appUri))
            {
                continue;
            }

            var configUri = BuildPlayerConfigUri(appUri);
            if (configUri is null)
            {
                continue;
            }

            string json;
            try
            {
                json = await httpClient.GetStringAsync(configUri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in ExtractCandidatesFromPlayerConfig(json))
            {
                dedup.Add(candidate);
            }
        }

        return dedup.ToList();
    }

    private static Uri? BuildPlayerConfigUri(Uri appUri)
    {
        if (!appUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !appUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = appUri.AbsolutePath;
        if (path.EndsWith("/playerConfig", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(appUri)
            {
                Query = string.Empty
            }.Uri;
        }

        var normalizedPath = path.EndsWith("/", StringComparison.Ordinal)
            ? path[..^1]
            : path;

        return new UriBuilder(appUri)
        {
            Path = $"{normalizedPath}/playerConfig",
            Query = string.Empty
        }.Uri;
    }

    private static IEnumerable<string> ExtractCandidatesFromPlayerConfig(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            string? streamAddress = null;
            if (root.TryGetProperty("streamAddress", out var streamAddressElement)
                && streamAddressElement.ValueKind == JsonValueKind.String)
            {
                streamAddress = streamAddressElement.GetString();
                if (!string.IsNullOrWhiteSpace(streamAddress))
                {
                    yield return streamAddress!;
                }
            }

            string? defaultMount = null;
            if (root.TryGetProperty("defaultMountUrl", out var defaultMountElement)
                && defaultMountElement.ValueKind == JsonValueKind.String)
            {
                defaultMount = defaultMountElement.GetString();
            }

            if (!string.IsNullOrWhiteSpace(streamAddress) && !string.IsNullOrWhiteSpace(defaultMount))
            {
                if (Uri.TryCreate(streamAddress, UriKind.Absolute, out var streamUri)
                    && Uri.TryCreate(streamUri, defaultMount, out var mountedUri))
                {
                    yield return mountedUri.ToString();
                }
            }

            if (root.TryGetProperty("mountPoints", out var mountPointsElement)
                && mountPointsElement.ValueKind == JsonValueKind.Array
                && !string.IsNullOrWhiteSpace(streamAddress)
                && Uri.TryCreate(streamAddress, UriKind.Absolute, out var streamBaseUri))
            {
                foreach (var mount in mountPointsElement.EnumerateArray())
                {
                    if (mount.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var mountValue = mount.GetString();
                    if (string.IsNullOrWhiteSpace(mountValue))
                    {
                        continue;
                    }

                    if (Uri.TryCreate(streamBaseUri, mountValue, out var mountUri))
                    {
                        yield return mountUri.ToString();
                    }
                }
            }

            if (root.TryGetProperty("generalLinks", out var generalLinksElement)
                && generalLinksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var linkObject in generalLinksElement.EnumerateArray())
                {
                    if (!linkObject.TryGetProperty("Link", out var linkElement)
                        || linkElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var linkValue = linkElement.GetString();
                    if (!string.IsNullOrWhiteSpace(linkValue))
                    {
                        yield return linkValue!;
                    }
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> ExpandCandidatesAsync(IReadOnlyList<string> seedCandidates, CancellationToken cancellationToken)
    {
        var ordered = new List<string>();
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seedCandidates)
        {
            foreach (var variant in EnumerateCandidateVariants(seed))
            {
                if (dedup.Add(variant))
                {
                    ordered.Add(variant);
                }
            }
        }

        foreach (var playlistSeed in seedCandidates)
        {
            var playlistTargets = await TryResolvePlaylistTargetsAsync(playlistSeed, cancellationToken).ConfigureAwait(false);
            foreach (var target in playlistTargets)
            {
                foreach (var variant in EnumerateCandidateVariants(target))
                {
                    if (dedup.Add(variant))
                    {
                        ordered.Add(variant);
                    }
                }
            }
        }

        return ordered;
    }

    private async Task<IReadOnlyList<string>> TryResolvePlaylistTargetsAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
        {
            return [];
        }

        if (!candidateUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !candidateUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!IsLikelyPlaylistEndpoint(candidateUri))
        {
            return [];
        }

        string payload;
        try
        {
            payload = await httpClient.GetStringAsync(candidateUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = line.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (value.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                var separator = value.IndexOf('=');
                if (separator > 0 && separator < value.Length - 1)
                {
                    value = value[(separator + 1)..].Trim();
                }
            }

            if (!Uri.TryCreate(candidateUri, value, out var resolved))
            {
                continue;
            }

            var resolvedValue = resolved.ToString();
            if (!LooksLikeStreamUrl(resolvedValue))
            {
                continue;
            }

            dedup.Add(resolvedValue);
        }

        return dedup.ToList();
    }

    private static IEnumerable<string> EnumerateCandidateVariants(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            yield break;
        }

        yield return candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            yield break;
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            var alternateScheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
            var schemeVariant = new UriBuilder(uri)
            {
                Scheme = alternateScheme,
                Port = uri.IsDefaultPort ? -1 : uri.Port
            }.Uri.ToString();

            if (!schemeVariant.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                yield return schemeVariant;
            }

            if (Uri.TryCreate(schemeVariant, UriKind.Absolute, out var schemeUri)
                && !string.IsNullOrWhiteSpace(schemeUri.Query))
            {
                var querylessSchemeVariant = new UriBuilder(schemeUri)
                {
                    Query = string.Empty
                }.Uri.ToString();

                if (!querylessSchemeVariant.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    && !querylessSchemeVariant.Equals(schemeVariant, StringComparison.OrdinalIgnoreCase))
                {
                    yield return querylessSchemeVariant;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var querylessVariant = new UriBuilder(uri)
            {
                Query = string.Empty
            }.Uri.ToString();

            if (!querylessVariant.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                yield return querylessVariant;
            }
        }

        if ((uri.Host.Equals("stream.zeno.fm", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(uri.Host, "^stream-[0-9]+\\.zeno\\.fm$", RegexOptions.IgnoreCase))
            && TryGetFirstPathSegment(uri, out var streamId))
        {
            yield return $"https://stream.zeno.fm/{streamId}";
            yield return $"http://stream.zeno.fm/{streamId}";
        }

        if (uri.AbsolutePath.EndsWith("/;", StringComparison.Ordinal))
        {
            var trimmedVariant = new UriBuilder(uri)
            {
                Path = uri.AbsolutePath[..^1]
            }.Uri.ToString();

            if (!trimmedVariant.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmedVariant;
            }
        }
    }

    private static bool IsLikelyPlaylistEndpoint(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".asx", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/listen/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/radio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetFirstPathSegment(Uri uri, out string segment)
    {
        segment = string.Empty;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        segment = parts[0];
        return !string.IsNullOrWhiteSpace(segment);
    }

    private static IEnumerable<Uri> EnumerateSecondaryResources(string html, Uri baseUri)
    {
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SrcOrHrefRegex.Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, raw, out var resolved))
            {
                continue;
            }

            if (!resolved.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !resolved.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsLikelySecondaryResource(resolved))
            {
                continue;
            }

            var resolvedValue = resolved.ToString();
            if (dedup.Add(resolvedValue))
            {
                yield return resolved;
            }
        }
    }

    private static bool IsLikelySecondaryResource(Uri uri)
    {
        var value = uri.ToString();
        var path = uri.AbsolutePath;

        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || value.Contains("player", StringComparison.OrdinalIgnoreCase)
            || value.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || value.Contains("radio", StringComparison.OrdinalIgnoreCase)
            || value.Contains("stream", StringComparison.OrdinalIgnoreCase)
            || value.Contains("live", StringComparison.OrdinalIgnoreCase)
            || value.Contains("listen", StringComparison.OrdinalIgnoreCase)
            || value.Contains("embed", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mount", StringComparison.OrdinalIgnoreCase)
            || value.Contains("config", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStreamUrl(string value)
    {
        // Reject VOD recordings (dated CDN paths, Triton recordings, etc.) before any other check.
        if (StartupStreamUrlHeuristics.IsLikelyVodRecording(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Host.Equals("stream.zeno.fm", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(absoluteUri.Host, "^stream-[0-9]+\\.zeno\\.fm$", RegexOptions.IgnoreCase)
                || absoluteUri.Host.Contains("listen2myradio", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Triton streamtheworld live-redirect URL: host is *.streamtheworld.com and the path
            // has a segment after /livestream-redirect/ (the callsign). The base URL without a
            // callsign (".../api/livestream-redirect") is intentionally excluded — it is not a
            // valid stream on its own.
            if (absoluteUri.Host.EndsWith(".streamtheworld.com", StringComparison.OrdinalIgnoreCase)
                || absoluteUri.Host.Equals("streamtheworld.com", StringComparison.OrdinalIgnoreCase))
            {
                var path = absoluteUri.AbsolutePath;
                var redirectIndex = path.IndexOf("/livestream-redirect/", StringComparison.OrdinalIgnoreCase);
                if (redirectIndex >= 0 && path.Length > redirectIndex + "/livestream-redirect/".Length)
                {
                    return true;
                }
            }
        }

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
            || value.Contains("/live", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/listen", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/radio", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/radio?", StringComparison.OrdinalIgnoreCase);
    }
}
