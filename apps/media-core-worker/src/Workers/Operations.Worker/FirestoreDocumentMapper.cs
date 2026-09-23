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
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
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
            media,
            streamUrl,
            primaryUrl: GetString(fields, "primaryUrl"),
            country: GetString(fields, "country"),
            fallbackStreamUrls: GetStringArray(fields, "fallbackStreamUrls"),
            isExcluded: GetBool(fields, "isExcluded", defaultValue: false));
    }

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
