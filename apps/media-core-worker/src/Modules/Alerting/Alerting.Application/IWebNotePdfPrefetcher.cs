namespace MediaOpsCore.Modules.Alerting.Application;

/// <summary>The page of a web alert that has just been stored, to be kept as PDF while it is still as it was found.</summary>
public sealed record WebNotePdfRequest(string AlertId, string Url, IReadOnlyList<string> Words, DateTimeOffset CapturedAt);

/// <summary>
/// Outbound port: makes the PDF copy of a web note at the moment its alert is created (the page may change or be
/// taken down before anybody edits the note). Fire and forget — it must never delay or fail the alert.
/// </summary>
public interface IWebNotePdfPrefetcher
{
    void Request(WebNotePdfRequest request);
}
