using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public static class StartupStreamUrlHeuristics
{
    // Matches 8-digit date segments in a URL path: /20260606/, /20260101/, etc.
    private static readonly Regex DatePathSegmentRegex = new(
        @"/\d{8}/",
        RegexOptions.Compiled);

    // Matches 6-digit YYYYMM folder segments: /202606/, /202601/, etc.
    private static readonly Regex YearMonthPathSegmentRegex = new(
        @"/\d{6}/",
        RegexOptions.Compiled);

    // Matches Triton Digital VOD recording filenames: 4982842_023447_audio_128.mp3
    private static readonly Regex TritonVodFilenameRegex = new(
        @"\d{5,}_\d{6}_audio[_\d]*\.(mp3|aac|opus|ogg)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the URL looks like a VOD recording rather than a live stream.
    /// Filters out dated CDN paths (Triton, prisa-co, etc.) that contain past recordings.
    /// </summary>
    public static bool IsLikelyVodRecording(string streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;

        // /20260606/ — date folder
        if (DatePathSegmentRegex.IsMatch(path))
        {
            return true;
        }

        // /202606/ — year+month folder (common in Triton CDN VOD paths)
        if (YearMonthPathSegmentRegex.IsMatch(path))
        {
            return true;
        }

        // Triton Digital VOD filename pattern: 4982842_023447_audio_128.mp3
        if (TritonVodFilenameRegex.IsMatch(path))
        {
            return true;
        }

        // Triton Digital VOD CDN: *.mc.tritondigital.com/*/media/*
        if (uri.Host.EndsWith(".tritondigital.com", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/media/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Omny podcast CDN: traffic.omny.fm/d/clips/ — never a live stream
        if (uri.Host.Equals("traffic.omny.fm", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/d/clips/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the fallback URLs that are structural variants of the current streamUrl and
    /// therefore safe candidates to try when the primary stream fails.
    /// Only two cases justify changing the streamUrl:
    ///   1. Same host+path+query, different scheme (http↔https).
    ///   2. Same host+path, different querystring (e.g. rotated token or updated params).
    /// Fallbacks with a different host are stored for mapping purposes but are never used
    /// for recovery — they could be VOD/podcast URLs unrelated to the live stream.
    /// </summary>
    public static IReadOnlyList<string> BuildConservativeCandidates(CaptureSource source)
    {
        if (!Uri.TryCreate(source.StreamUrl, UriKind.Absolute, out var current))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.StreamUrl };

        foreach (var url in source.FallbackStreamUrls)
        {
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var fallback))
            {
                continue;
            }

            // Must be the same host
            if (!string.Equals(fallback.Host, current.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Must be the same path (covers both http↔https toggle and token-rotated variants)
            if (!string.Equals(fallback.AbsolutePath, current.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(url);
        }

        return result;
    }

    public static bool IsLikelyEphemeral(string streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = uri.Query;
        return query.Contains("zt=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("sig=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("signature=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("expires=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("exp=", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExpiringSoon(string streamUrl, TimeSpan minTtl, DateTimeOffset nowUtc, out DateTimeOffset? expiresAtUtc)
    {
        expiresAtUtc = null;

        if (!TryGetTokenExpiryUtc(streamUrl, out var expiry))
        {
            return false;
        }

        expiresAtUtc = expiry;
        return expiry <= nowUtc.Add(minTtl);
    }

    public static bool TryGetTokenExpiryUtc(string streamUrl, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);

        if (query.TryGetValue("exp", out var expValue) && TryParseUnixSeconds(expValue, out expiresAtUtc))
        {
            return true;
        }

        if (!query.TryGetValue("zt", out var ztToken))
        {
            return false;
        }

        var tokenParts = ztToken.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokenParts.Length < 2)
        {
            return false;
        }

        if (!TryDecodeBase64Url(tokenParts[1], out var payloadJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("exp", out var expProperty))
        {
            return false;
        }

        long expSeconds;
        if (expProperty.ValueKind == JsonValueKind.Number && expProperty.TryGetInt64(out expSeconds))
        {
            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            return true;
        }

        if (expProperty.ValueKind == JsonValueKind.String && long.TryParse(expProperty.GetString(), out expSeconds))
        {
            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..separatorIndex]);
            var value = Uri.UnescapeDataString(part[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static bool TryParseUnixSeconds(string value, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        if (!long.TryParse(value, out var seconds))
        {
            return false;
        }

        expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static bool TryDecodeBase64Url(string value, out string decoded)
    {
        decoded = string.Empty;

        var normalized = value
            .Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal);

        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
