using System.Text;
using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

public static class StartupStreamUrlHeuristics
{
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
