using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Direct port of Monitor.processTranscription + Monitor.processAlert + Helper.GetAlertType /
// CheckIfIsAdd / CheckIfDuplicateOtherPlatform / GetContextAroundKeyword
// (media-monitor/apps/w-service/Monitor.cs:64-155, Helper.cs:283-362).
public sealed class DetectAlertsUseCase : IDetectAlertsUseCase
{
    // How far back GetPendingNotificationsAsync looks for incomplete deliveries at startup — a
    // story from before this window is stale enough that resending it unprompted isn't useful.
    private static readonly TimeSpan PendingNotificationLookback = TimeSpan.FromHours(24);

    private readonly IClientConfigRepository clientConfigRepository;
    private readonly IAlertRepository alertRepository;
    private readonly IAlertNotifier alertNotifier;

    public DetectAlertsUseCase(
        IClientConfigRepository clientConfigRepository,
        IAlertRepository alertRepository,
        IAlertNotifier alertNotifier)
    {
        this.clientConfigRepository = clientConfigRepository;
        this.alertRepository = alertRepository;
        this.alertNotifier = alertNotifier;
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
            var recipients = ResolveRecipients(client, media, alert);
            var alertId = await alertRepository.InsertAsync(alert, recipients, cancellationToken).ConfigureAwait(false);

            await NotifyAsync(alertId, alert, recipients, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RetryPendingNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var pending = await alertRepository
            .GetPendingNotificationsAsync(PendingNotificationLookback, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in pending)
        {
            await NotifyAsync(item.AlertId, item.Alert, item.PendingRecipients, cancellationToken).ConfigureAwait(false);
        }
    }

    // No recipients captured for an alert type that AlertMessageFormatter never turns into a
    // message (RepeatedWithinMinute) — otherwise it would sit in Mongo as "pending" forever with
    // nothing that RetryPendingNotificationsAsync could ever actually send.
    private static IReadOnlyList<string> ResolveRecipients(ClientKeywordConfig client, string media, Alert alert)
    {
        if (AlertMessageFormatter.Format(alert) is null)
        {
            return Array.Empty<string>();
        }

        return client.AlertRecipientsByMedia.TryGetValue(media, out var recipients) ? recipients : Array.Empty<string>();
    }

    private async Task NotifyAsync(string alertId, Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        // IAlertNotifier is best-effort by contract (see its doc comment) — it must not throw for
        // expected failures, so no try/catch is needed here. One client's notification never
        // blocks the next client's detection/persistence in this loop.
        var succeeded = await alertNotifier.NotifyAsync(alert, recipients, cancellationToken).ConfigureAwait(false);
        if (succeeded.Count > 0)
        {
            await alertRepository.MarkRecipientsNotifiedAsync(alertId, succeeded, cancellationToken).ConfigureAwait(false);
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

        if (await IsDuplicateOnOtherPlatformAsync(platform, client, text, matchedWords, endTime, cancellationToken).ConfigureAwait(false))
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
        DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        var platforms = await clientConfigRepository.GetPlatformNamesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var otherPlatform in platforms.Where(candidate => !string.Equals(candidate, platform, StringComparison.Ordinal)))
        {
            var lastAlert = await alertRepository
                .GetLastNewOrRepeatedAlertAsync(otherPlatform, client.Name, cancellationToken)
                .ConfigureAwait(false);

            // Same 60s window as the same-platform check above — without this, a match against
            // an alert from days ago (a stale/old story, or leftover test data) could still fire.
            if (lastAlert is null || (endTime - lastAlert.EndTime).TotalSeconds > 60)
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
