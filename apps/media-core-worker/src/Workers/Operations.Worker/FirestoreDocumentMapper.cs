using System.Text.Json;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Pure mapping between Firestore's REST "document" JSON shape (every field wrapped in a type
/// tag: {"stringValue": ...}, {"booleanValue": ...}, {"arrayValue": {"values": [...]}}) and
/// CaptureSource — kept free of HTTP/IO so it can be unit tested directly against a raw JSON
/// document. Only the handful of field types the "platforms" collection actually uses are
/// supported; Firestore's full value-type union (timestamps, maps, references, ...) is not
/// needed here.
/// </summary>
internal static class FirestoreDocumentMapper
{
    // A document with no sourceId is a WhatsApp-only platform entry (no associated capture
    // source) and is intentionally skipped rather than mapped.
    public static CaptureSource? TryMapCaptureSource(JsonElement document)
    {
        if (!document.TryGetProperty("fields", out var fields))
        {
            return null;
        }

        var sourceId = GetString(fields, "sourceId");
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var streamUrl = GetString(fields, "streamUrl");
        var primaryUrl = GetString(fields, "primaryUrl");
        var fallbackStreamUrls = GetStringArray(fields, "fallbackStreamUrls");
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            // No streamUrl yet must not mean "WhatsApp-only entry, skip it" for radio or
            // television alike — it may just mean nobody has typed the direct stream URL in
            // that specific box. Try whatever already-resolved URL is available instead of
            // requiring a throwaway value just to be recognized:
            //   1. fallbackStreamUrls — validated/known-working candidates, whether discovered
            //      automatically or pasted in manually via the Plataformas modal.
            //   2. primaryUrl, but only when it is itself a YouTube page — a plain radio/TV
            //      "URL de descubrimiento" is a webpage, not a playable stream, so it is not a
            //      usable placeholder; a YouTube channel/live page is different, because
            //      YtdlpLiveStreamUrlResolver.CanResolve keys off primaryUrl too and replaces
            //      this placeholder with the real resolved URL on the next startup cycle.
            var bootstrapUrl = fallbackStreamUrls?.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
                ?? (YtdlpLiveStreamUrlResolver.IsYouTubeUrl(primaryUrl) ? primaryUrl : null);

            if (bootstrapUrl is null)
            {
                return null;
            }

            streamUrl = bootstrapUrl;
        }

        var platform = GetString(fields, "name");
        var media = GetString(fields, "media");
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(media))
        {
            return null;
        }

        return new CaptureSource(
            sourceId,
            JsonFileCaptureSourceRepository.GlobalIngestionScopeId,
            platform,
            NormalizeMedia(media),
            streamUrl,
            primaryUrl: primaryUrl,
            country: GetString(fields, "country"),
            fallbackStreamUrls: fallbackStreamUrls,
            isExcluded: GetBool(fields, "isExcluded", defaultValue: false));
    }

    // The "platforms" collection's `media` value is whatever the web-ui's media-type catalog
    // uses ("tv") — reports and everything in web-api/web-ui key off that spelling, and stay
    // untouched. Only the worker's own in-memory CaptureSource needs "television" to match its
    // own allow-list/plugin-profile config; this mapping is never written back to Firestore.
    private static string NormalizeMedia(string media) =>
        media.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "television" : media;

    public static object StringValue(string value) => new { stringValue = value };

    public static object BoolValue(bool value) => new { booleanValue = value };

    // Firestore encodes 64-bit integers as strings in JSON to survive languages without them.
    public static object IntegerValue(long value) => new { integerValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    public static object DoubleValue(double value) => new { doubleValue = value };

    public static object TimestampValue(DateTimeOffset value) => new { timestampValue = value.UtcDateTime.ToString("o") };

    public static object StringArrayValue(IEnumerable<string> values) => new
    {
        arrayValue = new { values = values.Select(v => new { stringValue = v }).ToArray() },
    };

    private static string? GetString(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("stringValue", out var value)
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement fields, string name, bool defaultValue) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("booleanValue", out var value)
            ? value.GetBoolean()
            : defaultValue;

    private static IReadOnlyList<string>? GetStringArray(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var field) || !field.TryGetProperty("arrayValue", out var arrayValue))
        {
            return null;
        }

        if (!arrayValue.TryGetProperty("values", out var values))
        {
            return Array.Empty<string>();
        }

        return values.EnumerateArray()
            .Select(v => v.TryGetProperty("stringValue", out var sv) ? sv.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray();
    }
}
