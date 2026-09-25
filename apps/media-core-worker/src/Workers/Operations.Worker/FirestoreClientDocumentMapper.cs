using System.Text.Json;
using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Pure mapping between Firestore's REST "document" JSON shape and ClientKeywordConfig — same
/// style as FirestoreDocumentMapper (capture sources), kept free of HTTP/IO. Only the field
/// shapes apps/web-api's `clients` collection actually writes are supported (ClientDto:
/// name, words[].value / words[].adds[].before|after, alerts as mediaName -> string[] —
/// see packages/shared/models/clients.dto.ts).
/// </summary>
internal static class FirestoreClientDocumentMapper
{
    public static ClientKeywordConfig? TryMapClient(JsonElement document)
    {
        if (!document.TryGetProperty("fields", out var fields))
        {
            return null;
        }

        var name = GetString(fields, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var keywords = GetMapArray(fields, "words")
            .Select(ToKeywordConfig)
            .Where(keyword => keyword is not null)
            .Select(keyword => keyword!)
            .ToArray();

        return new ClientKeywordConfig(name, keywords, GetStringArrayMap(fields, "alerts"));
    }

    private static KeywordConfig? ToKeywordConfig(JsonElement wordFields)
    {
        var value = GetString(wordFields, "value");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var adds = GetMapArray(wordFields, "adds")
            .Select(addFields => new KeywordAdContext(
                GetString(addFields, "before") ?? string.Empty,
                GetString(addFields, "after") ?? string.Empty))
            .ToArray();

        return new KeywordConfig(value, adds);
    }

    private static string? GetString(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("stringValue", out var value)
            ? value.GetString()
            : null;

    // Yields the "fields" object of each map in a Firestore arrayValue-of-mapValue field
    // (e.g. words, words[].adds).
    private static IEnumerable<JsonElement> GetMapArray(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var field) || !field.TryGetProperty("arrayValue", out var arrayValue))
        {
            yield break;
        }

        if (!arrayValue.TryGetProperty("values", out var values))
        {
            yield break;
        }

        foreach (var item in values.EnumerateArray())
        {
            if (item.TryGetProperty("mapValue", out var mapValue) && mapValue.TryGetProperty("fields", out var mapFields))
            {
                yield return mapFields;
            }
        }
    }

    // "alerts" is a Firestore mapValue field keyed by media name, each value an arrayValue of
    // stringValue (WhatsApp numbers).
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GetStringArrayMap(JsonElement fields, string name)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (!fields.TryGetProperty(name, out var field) || !field.TryGetProperty("mapValue", out var mapValue)
            || !mapValue.TryGetProperty("fields", out var mapFields))
        {
            return result;
        }

        foreach (var property in mapFields.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("arrayValue", out var arrayValue) || !arrayValue.TryGetProperty("values", out var values))
            {
                continue;
            }

            var numbers = values.EnumerateArray()
                .Select(value => value.TryGetProperty("stringValue", out var stringValue) ? stringValue.GetString() : null)
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Select(number => number!)
                .ToArray();

            result[property.Name] = numbers;
        }

        return result;
    }
}
