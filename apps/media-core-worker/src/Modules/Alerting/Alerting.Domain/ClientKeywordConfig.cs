namespace MediaOpsCore.Modules.Alerting.Domain;

// "afer" (not "after") is a real typo already stored in production config.client documents
// (media-monitor/apps/w-service/Helper.cs:340) — the ad-context match only ever worked against
// that misspelled key, so it must be read verbatim rather than "corrected".
public sealed record KeywordAdContext(string Before, string After);

public sealed record KeywordConfig(string Value, IReadOnlyList<KeywordAdContext> Adds);

public sealed record ClientKeywordConfig(
    string Name,
    IReadOnlyList<KeywordConfig> Keywords,
    IReadOnlyList<string> NumbersRadio,
    IReadOnlyList<string> NumbersTv);
