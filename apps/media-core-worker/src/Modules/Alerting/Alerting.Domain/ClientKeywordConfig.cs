namespace MediaOpsCore.Modules.Alerting.Domain;

public sealed record KeywordAdContext(string Before, string After);

public sealed record KeywordConfig(string Value, IReadOnlyList<KeywordAdContext> Adds);

// AlertRecipientsByMedia mirrors Firestore's `clients.alerts` field (packages/shared/models/clients.dto.ts):
// mediaName -> WhatsApp numbers for that media, for any media type in the `media_types` catalog
// (internet/radio/tv/prensa/redes), not just radio/tv.
public sealed record ClientKeywordConfig(
    string Name,
    IReadOnlyList<KeywordConfig> Keywords,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AlertRecipientsByMedia);
