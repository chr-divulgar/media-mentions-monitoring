namespace MediaOpsCore.Modules.Capture.Domain;

public sealed class CaptureSource
{
    private const string DefaultCountry = "colombia";

    private static readonly IReadOnlyDictionary<string, int> UtcOffsetMinutesByCountry =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["colombia"] = -5 * 60,
            ["mexico"] = -6 * 60,
            ["argentina"] = -3 * 60,
            ["chile"] = -4 * 60,
            ["peru"] = -5 * 60,
            ["ecuador"] = -5 * 60,
            ["panama"] = -5 * 60,
            ["venezuela"] = -4 * 60,
            ["spain"] = 1 * 60,
            ["españa"] = 1 * 60,
            ["united states"] = -5 * 60,
            ["usa"] = -5 * 60,
            ["canada"] = -5 * 60
        };

    public CaptureSource(
        string sourceId,
        string tenantId,
        string platform,
        string media,
        string streamUrl,
        string? primaryUrl = null,
        string? country = null,
        int? utcOffsetMinutes = null,
        IReadOnlyList<string>? fallbackStreamUrls = null,
        bool isExcluded = false)
    {
        SourceId = string.IsNullOrWhiteSpace(sourceId) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(sourceId)) : sourceId;
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(tenantId)) : tenantId;
        Platform = string.IsNullOrWhiteSpace(platform) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(platform)) : platform;
        Media = string.IsNullOrWhiteSpace(media) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(media)) : media;
        StreamUrl = string.IsNullOrWhiteSpace(streamUrl) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(streamUrl)) : streamUrl;
        PrimaryUrl = string.IsNullOrWhiteSpace(primaryUrl) ? null : primaryUrl;
        FallbackStreamUrls = fallbackStreamUrls is { Count: > 0 }
            ? fallbackStreamUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray()
            : [];

        Country = string.IsNullOrWhiteSpace(country)
            ? DefaultCountry
            : country.Trim().ToLowerInvariant();

        UtcOffsetMinutes = utcOffsetMinutes ?? ResolveUtcOffsetMinutes(Country);
        IsExcluded = isExcluded;
    }

    public string SourceId { get; }

    public string TenantId { get; }

    public string Platform { get; }

    public string Media { get; }

    public string StreamUrl { get; }

    public string? PrimaryUrl { get; }

    public IReadOnlyList<string> FallbackStreamUrls { get; }

    public string Country { get; }

    public int UtcOffsetMinutes { get; }

    public bool IsExcluded { get; }

    public CaptureSource WithStreamUrl(string streamUrl)
    {
        return new CaptureSource(SourceId, TenantId, Platform, Media, streamUrl, PrimaryUrl, Country, UtcOffsetMinutes, FallbackStreamUrls, IsExcluded);
    }

    public CaptureSource WithFallbackStreamUrls(IReadOnlyList<string> fallbackStreamUrls)
    {
        return new CaptureSource(SourceId, TenantId, Platform, Media, StreamUrl, PrimaryUrl, Country, UtcOffsetMinutes, fallbackStreamUrls, IsExcluded);
    }

    public CaptureSource WithExcluded(bool isExcluded)
    {
        return new CaptureSource(SourceId, TenantId, Platform, Media, StreamUrl, PrimaryUrl, Country, UtcOffsetMinutes, FallbackStreamUrls, isExcluded);
    }

    private static int ResolveUtcOffsetMinutes(string country)
    {
        return UtcOffsetMinutesByCountry.TryGetValue(country, out var offsetMinutes)
            ? offsetMinutes
            : UtcOffsetMinutesByCountry[DefaultCountry];
    }
}