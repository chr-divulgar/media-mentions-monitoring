namespace MediaOpsCore.Modules.Alerting.Application;

public interface IDetectAlertsUseCase
{
    // Called once per recognized transcription chunk. Equivalent to
    // Monitor.processTranscription (media-monitor/apps/w-service/Monitor.cs:64-120), invoked directly
    // in-process instead of polling an unprocessed-transcription collection every 10 seconds.
    Task ExecuteAsync(
        string platform,
        string media,
        string filePath,
        string text,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default);

    // Called once at worker startup: resends any alert notification left incomplete by a process
    // restart or a WhatsApp sidecar outage between detection and delivery.
    Task RetryPendingNotificationsAsync(CancellationToken cancellationToken = default);

    // Web-news mention: a client match here is a client the article is OR isn't about, so none of
    // the audio-oriented rules apply — no ad detection, no "repeated within 60s" (two unrelated
    // articles from the same outlet 30s apart are not a duplicate), no cross-platform check (web
    // outlets aren't in the capture-source platform list that check runs against). Deduplicates by
    // (url, client) instead, through IAlertRepository.ExistsForUrlAsync.
    Task<WebArticleAlertOutcome> ExecuteWebArticleAsync(
        string outlet,
        string url,
        string title,
        string firstParagraph,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);
}

// What WebNewsCaptureUseCase needs back to decide the article's final status and what to log:
// which clients actually matched (empty means WebArticleStatus.NoKeyword), and how many of the
// intended recipients were confirmed sent (fewer than RecipientCount means a retry will follow).
public sealed record WebArticleAlertOutcome(
    IReadOnlyList<string> MatchedClients,
    IReadOnlyList<string> MatchedKeywords,
    int RecipientCount,
    int NotifiedCount);
