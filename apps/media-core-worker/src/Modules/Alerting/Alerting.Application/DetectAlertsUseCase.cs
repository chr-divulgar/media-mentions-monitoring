using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Direct port of Monitor.processTranscription + Monitor.processAlert + Helper.GetAlertType /
// CheckIfIsAdd / CheckIfDuplicateOtherPlatform / GetContextAroundKeyword
// (media-monitor/apps/w-service/Monitor.cs:64-155, Helper.cs:283-362).
public sealed class DetectAlertsUseCase : IDetectAlertsUseCase
{
    private readonly IClientConfigRepository clientConfigRepository;
    private readonly IAlertRepository alertRepository;

    public DetectAlertsUseCase(IClientConfigRepository clientConfigRepository, IAlertRepository alertRepository)
    {
        this.clientConfigRepository = clientConfigRepository;
        this.alertRepository = alertRepository;
    }

    public async Task ExecuteAsync(
        string platform,
        string media,
        string filePath,
        string text,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var client in clients)
        {
            var matchedWords = client.Keywords
                .Select(keyword => keyword.Value)
                .Where(value => !string.IsNullOrEmpty(value) && text.Contains(value, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (matchedWords.Length == 0)
            {
                continue;
            }

            var type = await ClassifyAsync(platform, client, text, matchedWords, endTime, cancellationToken).ConfigureAwait(false);

            var alert = new Alert(text, startTime, endTime, media, platform, filePath, matchedWords, client.Name, type);
            await alertRepository.InsertAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AlertType> ClassifyAsync(
        string platform,
        ClientKeywordConfig client,
        string text,
        IReadOnlyList<string> matchedWords,
        DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        if (IsAd(client, text, matchedWords))
        {
            return AlertType.Ad;
        }

        var lastOwnPlatformAlert = await alertRepository
            .GetLastNewOrRepeatedAlertAsync(platform, client.Name, cancellationToken)
            .ConfigureAwait(false);

        if (lastOwnPlatformAlert is not null && (endTime - lastOwnPlatformAlert.EndTime).TotalSeconds <= 60)
        {
            return AlertType.RepeatedWithinMinute;
        }

        if (await IsDuplicateOnOtherPlatformAsync(platform, client, text, matchedWords, cancellationToken).ConfigureAwait(false))
        {
            return AlertType.RepeatedOtherPlatform;
        }

        return AlertType.New;
    }

    private static bool IsAd(ClientKeywordConfig client, string text, IReadOnlyList<string> matchedWords)
    {
        foreach (var keyword in client.Keywords)
        {
            if (!matchedWords.Contains(keyword.Value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var ad in keyword.Adds)
            {
                if (!string.IsNullOrEmpty(ad.Before) && text.Contains(ad.Before, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(ad.After) && text.Contains(ad.After, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> IsDuplicateOnOtherPlatformAsync(
        string platform,
        ClientKeywordConfig client,
        string text,
        IReadOnlyList<string> matchedWords,
        CancellationToken cancellationToken)
    {
        var platforms = await clientConfigRepository.GetPlatformNamesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var otherPlatform in platforms.Where(candidate => !string.Equals(candidate, platform, StringComparison.Ordinal)))
        {
            var lastAlert = await alertRepository
                .GetLastNewOrRepeatedAlertAsync(otherPlatform, client.Name, cancellationToken)
                .ConfigureAwait(false);

            if (lastAlert is null)
            {
                continue;
            }

            foreach (var keyword in matchedWords)
            {
                var (before, after) = GetContextAroundKeyword(text, keyword);
                if (before.Length > 0 && lastAlert.Text.Contains(before, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (after.Length > 0 && lastAlert.Text.Contains(after, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (string Before, string After) GetContextAroundKeyword(string text, string keyword)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keywordIndex = Array.FindIndex(words, word => string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase));
        if (keywordIndex < 0)
        {
            return (string.Empty, string.Empty);
        }

        var start = Math.Max(0, keywordIndex - 6);
        var end = Math.Min(words.Length, keywordIndex + 7);

        var before = string.Join(" ", words.Skip(start).Take(keywordIndex - start));
        var after = string.Join(" ", words.Skip(keywordIndex + 1).Take(end - keywordIndex - 1));
        return (before, after);
    }
}
