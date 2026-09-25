using System.Globalization;
using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Direct port of Monitor.processAlert's message assembly + Helper.ExtractKeywordContext /
// Helper.ReplaceCaseInsensitive (media-monitor/apps/w-service/Monitor.cs:135-154,
// Helper.cs:363-401). One deliberate fix from the legacy source: that code embeds the two-
// character literal "\n" (backslash + n) instead of a real line break, so WhatsApp/mudslide
// renders visible "\n" text instead of a newline — here real newlines are used instead.
public static class AlertMessageFormatter
{
    // No message for RepeatedWithinMinute — matches Monitor.cs:134 (early return before the
    // message is even built for that type).
    public static string? Format(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (alert.Type == AlertType.RepeatedWithinMinute)
        {
            return null;
        }

        var body = alert.Type switch
        {
            AlertType.RepeatedOtherPlatform => "Repetida",
            AlertType.Ad => "Anuncio",
            _ => HighlightKeywords(ExtractKeywordContext(alert.Text, alert.Words), alert.Words),
        };

        var titleCaseKeywords = new CultureInfo("en-US", useUserOverride: false).TextInfo
            .ToTitleCase(string.Join(" , ", alert.Words));

        return $"*{titleCaseKeywords}*\n*{alert.Platform}*\n({alert.StartTime:HH:mm:ss}_{alert.EndTime:HH:mm:ss})\n{body}";
    }

    // ~6 words of context before/after each keyword's first occurrence, deduped per keyword,
    // ordered by first appearance in the text, joined with no separator (each chunk already
    // carries its own trailing " ... ").
    private static string ExtractKeywordContext(string text, IReadOnlyList<string> keywords)
    {
        var words = text.Split(' ');
        var results = new List<string>();

        foreach (var keyword in keywords)
        {
            var keywordIndex = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (keywordIndex < 0)
            {
                continue;
            }

            var keywordWordIndex = text[..keywordIndex].Split(' ').Length - 1;
            var startIndex = Math.Max(0, keywordWordIndex - 6);
            var endIndex = Math.Min(words.Length - 1, keywordWordIndex + keyword.Split(' ').Length + 6 - 1);

            var context = string.Join(" ", words.Skip(startIndex).Take(endIndex - startIndex + 1));

            if (!results.Any(existing => existing.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(context + " ... ");
            }
        }

        results.Sort((a, b) =>
            text.IndexOf(a.TrimEnd('.', ' '), StringComparison.Ordinal)
                .CompareTo(text.IndexOf(b.TrimEnd('.', ' '), StringComparison.Ordinal)));

        var joined = string.Concat(results);
        return joined.Length >= 5 ? joined[..^5] : joined;
    }

    private static string HighlightKeywords(string text, IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            text = ReplaceCaseInsensitive(text, keyword, $"*{keyword.ToUpperInvariant()}*");
        }

        return text;
    }

    private static string ReplaceCaseInsensitive(string original, string oldValue, string newValue)
    {
        var index = original.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index != -1)
        {
            original = original.Remove(index, oldValue.Length).Insert(index, newValue);
            index = original.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
        }

        return original;
    }
}
