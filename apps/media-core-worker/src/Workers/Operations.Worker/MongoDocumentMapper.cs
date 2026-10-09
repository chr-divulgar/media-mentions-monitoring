using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Modules.Capture.Domain;
using MongoDB.Bson;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Pure mapping from the documents apps/web-api keeps in MongoDB (`config.platforms`,
/// `config.clients`, `config.users`) to the worker's own types, free of any database access so it
/// is tested directly against a document. Field names are the ones in
/// packages/shared/models (settings.dto.ts, clients.dto.ts).
/// </summary>
internal static class MongoDocumentMapper
{
    internal const string GlobalIngestionScopeId = "global-ingestion";

    // A document with no sourceId is a WhatsApp-only platform entry (no associated capture
    // source) and is intentionally skipped rather than mapped.
    public static CaptureSource? TryMapCaptureSource(BsonDocument document)
    {
        var sourceId = GetString(document, "sourceId");
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var streamUrl = GetString(document, "streamUrl");
        var primaryUrl = GetString(document, "primaryUrl");
        var fallbackStreamUrls = GetStringArray(document, "fallbackStreamUrls");
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
            var bootstrapUrl = fallbackStreamUrls?.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
                ?? (YtdlpLiveStreamUrlResolver.IsYouTubeUrl(primaryUrl) ? primaryUrl : null);

            if (bootstrapUrl is null)
            {
                return null;
            }

            streamUrl = bootstrapUrl;
        }

        var platform = GetString(document, "name");
        var media = GetString(document, "media");
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(media))
        {
            return null;
        }

        return new CaptureSource(
            sourceId,
            GlobalIngestionScopeId,
            platform,
            NormalizeMedia(media),
            streamUrl,
            primaryUrl: primaryUrl,
            country: GetString(document, "country"),
            fallbackStreamUrls: fallbackStreamUrls,
            isExcluded: document.TryGetValue("isExcluded", out var excluded) && excluded.IsBoolean && excluded.AsBoolean);
    }

    // The `media` value is whatever the web-ui's media-type catalogue uses ("tv"): reports and
    // everything in web-api/web-ui key off that spelling, and stay untouched. Only the worker's own
    // in-memory CaptureSource needs "television" to match its allow-list/plugin-profile config;
    // this mapping is never written back.
    private static string NormalizeMedia(string media) =>
        media.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "television" : media;

    public static ClientKeywordConfig? TryMapClient(BsonDocument document)
    {
        var name = GetString(document, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var keywords = GetDocuments(document, "words")
            .Select(ToKeywordConfig)
            .Where(keyword => keyword is not null)
            .Select(keyword => keyword!)
            .ToArray();

        return new ClientKeywordConfig(name, keywords, GetStringArrayMap(document, "alerts"), GetStringArrayMap(document, "alertEmails"));
    }

    private static KeywordConfig? ToKeywordConfig(BsonDocument word)
    {
        var value = GetString(word, "value");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var adds = GetDocuments(word, "adds")
            .Select(add => new KeywordAdContext(GetString(add, "before") ?? string.Empty, GetString(add, "after") ?? string.Empty))
            .ToArray();

        return new KeywordConfig(value, adds, AlertLevelExtensions.Parse(GetString(word, "level")));
    }

    /// <summary>A user with `notifyPlatformErrors` on, and how to reach them.</summary>
    internal sealed record OptedInUser(string? Phone, string? Email);

    public static OptedInUser? TryMapOptedInUser(BsonDocument document)
    {
        if (!document.TryGetValue("notifyPlatformErrors", out var flag) || !flag.IsBoolean || !flag.AsBoolean)
        {
            return null;
        }

        var phone = GetString(document, "phone");
        var email = GetString(document, "email");
        return string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email)
            ? null
            : new OptedInUser(string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), string.IsNullOrWhiteSpace(email) ? null : email.Trim());
    }

    private static string? GetString(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;

    private static IReadOnlyList<string>? GetStringArray(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value) || !value.IsBsonArray)
        {
            return null;
        }

        return value.AsBsonArray
            .Where(item => item.IsString && !string.IsNullOrWhiteSpace(item.AsString))
            .Select(item => item.AsString)
            .ToArray();
    }

    private static IEnumerable<BsonDocument> GetDocuments(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsBsonArray
            ? value.AsBsonArray.Where(item => item.IsBsonDocument).Select(item => item.AsBsonDocument)
            : [];

    // "alerts" and "alertEmails" are documents keyed by media name, each an array of recipients.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GetStringArrayMap(BsonDocument document, string name)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!document.TryGetValue(name, out var value) || !value.IsBsonDocument)
        {
            return result;
        }

        foreach (var element in value.AsBsonDocument)
        {
            if (!element.Value.IsBsonArray)
            {
                continue;
            }

            result[element.Name] = element.Value.AsBsonArray
                .Where(item => item.IsString && !string.IsNullOrWhiteSpace(item.AsString))
                .Select(item => item.AsString)
                .ToArray();
        }

        return result;
    }
}
