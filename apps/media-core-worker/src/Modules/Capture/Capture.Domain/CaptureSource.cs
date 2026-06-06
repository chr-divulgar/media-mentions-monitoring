namespace MediaOpsCore.Modules.Capture.Domain;

public sealed class CaptureSource
{
    public CaptureSource(string sourceId, string tenantId, string platform, string media, string streamUrl, string? primaryUrl = null)
    {
        SourceId = string.IsNullOrWhiteSpace(sourceId) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(sourceId)) : sourceId;
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(tenantId)) : tenantId;
        Platform = string.IsNullOrWhiteSpace(platform) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(platform)) : platform;
        Media = string.IsNullOrWhiteSpace(media) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(media)) : media;
        StreamUrl = string.IsNullOrWhiteSpace(streamUrl) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(streamUrl)) : streamUrl;
        PrimaryUrl = string.IsNullOrWhiteSpace(primaryUrl) ? null : primaryUrl;
    }

    public string SourceId { get; }

    public string TenantId { get; }

    public string Platform { get; }

    public string Media { get; }

    public string StreamUrl { get; }

    public string? PrimaryUrl { get; }

    public CaptureSource WithStreamUrl(string streamUrl)
    {
        return new CaptureSource(SourceId, TenantId, Platform, Media, streamUrl, PrimaryUrl);
    }
}