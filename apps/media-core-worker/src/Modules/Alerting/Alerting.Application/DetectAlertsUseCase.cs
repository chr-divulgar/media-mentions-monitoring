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

    // Every capture reaches detection on its own thread. Deciding "is this a repeat?" means looking for
    // the other stations' alerts, so two stations airing the same story at the same moment each looked
    // before the other had stored anything, and both came out "Nueva". Classifying and storing as one
    // step, one alert at a time, makes the second one see the first. Notification stays outside it.
    // ponytail: one gate for every client/station (both steps are a few ms); per-client gates if detection volume grows.
    private readonly SemaphoreSlim classifyAndStoreGate = new(1, 1);

    private readonly IClientConfigRepository clientConfigRepository;
    private readonly IAlertRepository alertRepository;
    private readonly IAlertNotifier alertNotifier;
    private readonly IWebNotePdfPrefetcher? pdfPrefetcher;

    public DetectAlertsUseCase(
        IClientConfigRepository clientConfigRepository,
        IAlertRepository alertRepository,
        IAlertNotifier alertNotifier,
        IWebNotePdfPrefetcher? pdfPrefetcher = null)
    {
        this.pdfPrefetcher = pdfPrefetcher;
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

        media = NormalizeMedia(media);
        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var client in clients)
        {
            var matchedWords = MatchedKeywords(client, text);

            if (matchedWords.Length == 0)
            {
                continue;
            }

            Alert alert;
            IReadOnlyList<string> recipients;
            string alertId;
            await classifyAndStoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (type, repeatOfAlertId) = await ClassifyAsync(platform, media, client, text, matchedWords, endTime, cancellationToken).ConfigureAwait(false);

                alert = new Alert(
                    text, startTime, endTime, media, platform, filePath, matchedWords, client.Name, type,
                    level: HighestLevel(client, matchedWords))
                {
                    RepeatOfAlertId = repeatOfAlertId,
                };
                recipients = ResolveRecipients(client, media, alert);
                alertId = await alertRepository.InsertAsync(alert, recipients, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                classifyAndStoreGate.Release();
            }

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

    public async Task<WebArticleAlertOutcome> ExecuteWebArticleAsync(
        string outlet,
        string url,
        string title,
        string firstParagraph,
        DateTimeOffset publishedAt,
        string? matchText = null,
        CancellationToken cancellationToken = default)
    {
        // Title AND the full article body (falling back to just the lead paragraph when no more
        // body text was captured) both count as "the text" for keyword matching — a keyword that
        // only shows up in the headline, or several paragraphs into the story, must still match.
        var text = $"{title}\n{matchText ?? firstParagraph}";
        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);

        var matchedClients = new List<string>();
        var matchedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recipientCount = 0;
        var notifiedCount = 0;

        foreach (var client in clients)
        {
            var clientMatchedWords = MatchedKeywords(client, text);

            if (clientMatchedWords.Length == 0)
            {
                continue;
            }

            if (await alertRepository.ExistsForUrlAsync(url, client.Name, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            matchedClients.Add(client.Name);
            foreach (var word in clientMatchedWords)
            {
                matchedKeywords.Add(word);
            }

            var alert = new Alert(
                firstParagraph, publishedAt, publishedAt, "internet", outlet, string.Empty,
                clientMatchedWords, client.Name, AlertType.New, title, url,
                level: HighestLevel(client, clientMatchedWords),
                capturedAt: DateTimeOffset.UtcNow);
            var recipients = ResolveRecipients(client, "internet", alert);
            var alertId = await alertRepository.InsertAsync(alert, recipients, cancellationToken).ConfigureAwait(false);

            // The PDF of the note is made now, in the background, for this alert's own words: never a reason to hold the alert.
            try
            {
                pdfPrefetcher?.Request(new WebNotePdfRequest(alertId, url, clientMatchedWords, alert.CapturedAt));
            }
            catch (Exception)
            {
                // Without it the PDF is simply made when the note is opened.
            }

            recipientCount += recipients.Count;
            notifiedCount += (await NotifyAsync(alertId, alert, recipients, cancellationToken).ConfigureAwait(false)).Count;
        }

        return new WebArticleAlertOutcome(matchedClients, matchedKeywords.ToArray(), recipientCount, notifiedCount);
    }

    public async Task<int> ExecuteUnreadableWebArticleAsync(
        string outlet,
        string url,
        string title,
        string reason,
        string? firstParagraph = null,
        string? matchText = null,
        CancellationToken cancellationToken = default)
    {
        var text = $"{title}\n{matchText}";
        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);
        // Its real publication time is unknown, so the alert is dated when it was found.
        var detectedAt = DateTimeOffset.UtcNow;
        var stored = 0;

        foreach (var client in clients)
        {
            var matchedWords = MatchedKeywords(client, text);
            if (matchedWords.Length == 0
                || await alertRepository.ExistsForUrlAsync(url, client.Name, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var alert = new Alert(
                firstParagraph ?? string.Empty, detectedAt, detectedAt, "internet", outlet, string.Empty,
                matchedWords, client.Name, AlertType.New, title, url,
                level: HighestLevel(client, matchedWords),
                capturedAt: detectedAt)
            {
                ReviewReason = reason,
            };

            // No recipients: nothing to send, so nothing is ever left "pending" for the retry worker.
            await alertRepository.InsertAsync(alert, Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
            stored++;
        }

        return stored;
    }

    private static string[] MatchedKeywords(ClientKeywordConfig client, string text) =>
        client.Keywords
            .Select(keyword => keyword.Value)
            .Where(value => !string.IsNullOrEmpty(value) && text.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // A mention is as severe as the most severe keyword it matched ("derrame" alone makes the whole
    // alert N4 even when "petroleo", also matched, is N1).
    private static AlertLevel HighestLevel(ClientKeywordConfig client, IReadOnlyList<string> matchedWords) =>
        client.Keywords
            .Where(keyword => matchedWords.Contains(keyword.Value, StringComparer.OrdinalIgnoreCase))
            .Select(keyword => keyword.Level)
            .DefaultIfEmpty(AlertLevel.Informative)
            .Max();

    // CaptureSource.Media says "television" — the capture module's own plugin/profile vocabulary
    // (MongoDocumentMapper.NormalizeMedia's doc comment: "Only the worker's own in-memory
    // CaptureSource needs 'television'"). Every client's AlertRecipientsByMedia and the portal's
    // media-type catalog say "tv", so without this, TV alerts always matched zero recipients.
    private static string NormalizeMedia(string media) =>
        media.Equals("television", StringComparison.OrdinalIgnoreCase) ? "tv" : media;

    // No recipients captured for an alert type that AlertMessageFormatter never turns into a
    // message (RepeatedWithinMinute) — otherwise it would sit in Mongo as "pending" forever with
    // nothing that RetryPendingNotificationsAsync could ever actually send.
    private static IReadOnlyList<string> ResolveRecipients(ClientKeywordConfig client, string media, Alert alert)
    {
        if (AlertMessageFormatter.Format(alert) is null)
        {
            return Array.Empty<string>();
        }

        var phones = client.AlertRecipientsByMedia.TryGetValue(media, out var numbers) ? numbers : Array.Empty<string>();
        var emails = client.AlertEmailsByMedia is not null && client.AlertEmailsByMedia.TryGetValue(media, out var addresses)
            ? addresses
            : Array.Empty<string>();

        return emails.Count == 0 ? phones : phones.Concat(emails).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<string>> NotifyAsync(string alertId, Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return Array.Empty<string>();
        }

        // IAlertNotifier is best-effort by contract (see its doc comment) — it must not throw for
        // expected failures, so no try/catch is needed here. One client's notification never
        // blocks the next client's detection/persistence in this loop.
        var succeeded = await alertNotifier.NotifyAsync(alert, recipients, cancellationToken).ConfigureAwait(false);
        if (succeeded.Count > 0)
        {
            await alertRepository.MarkRecipientsNotifiedAsync(alertId, succeeded, cancellationToken).ConfigureAwait(false);
        }

        return succeeded;
    }

    // The alert's type and, for a repeat, the id of the "Nueva" alert it repeats — decided here, once,
    // so nothing downstream has to guess which story a repeat belongs to.
    private async Task<(AlertType Type, string? RepeatOfAlertId)> ClassifyAsync(
        string platform,
        string media,
        ClientKeywordConfig client,
        string text,
        IReadOnlyList<string> matchedWords,
        DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        if (IsAd(client, text, matchedWords))
        {
            return (AlertType.Ad, null);
        }

        var lastOwnPlatformAlert = await alertRepository
            .GetLastNewOrRepeatedAlertAsync(platform, client.Name, media, cancellationToken)
            .ConfigureAwait(false);

        if (lastOwnPlatformAlert is not null && (endTime - lastOwnPlatformAlert.EndTime).TotalSeconds <= 60)
        {
            return (AlertType.RepeatedWithinMinute, StoryRoot(lastOwnPlatformAlert));
        }

        var duplicated = await FindDuplicateOnOtherPlatformAsync(platform, media, client, text, matchedWords, endTime, cancellationToken).ConfigureAwait(false);
        if (duplicated is not null)
        {
            return (AlertType.RepeatedOtherPlatform, StoryRoot(duplicated));
        }

        return (AlertType.New, null);
    }

    // A repeat of a repeat still belongs to the story's first alert, so every repeat of one story
    // points at the same "Nueva".
    private static string? StoryRoot(Alert alert) =>
        alert.Type == AlertType.New ? alert.Id : alert.RepeatOfAlertId ?? alert.Id;

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

    private async Task<Alert?> FindDuplicateOnOtherPlatformAsync(
        string platform,
        string media,
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
                .GetLastNewOrRepeatedAlertAsync(otherPlatform, client.Name, media, cancellationToken)
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
                    return lastAlert;
                }

                if (after.Length > 0 && lastAlert.Text.Contains(after, StringComparison.OrdinalIgnoreCase))
                {
                    return lastAlert;
                }
            }
        }

        return null;
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
