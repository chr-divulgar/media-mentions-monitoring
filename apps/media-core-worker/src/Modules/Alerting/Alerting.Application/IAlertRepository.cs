using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Equivalent to Helper.InsertAlert + Helper.GetLastNewOrRepeatedAlert
// (media-monitor/apps/w-service/Helper.cs:416-472). During the shadow-run validation period the
// implementation targets a collection separate from the legacy monitoring.alert (see
// MongoAlertingOptions.AlertCollectionName) so this repository's own dedup lookups never mix with,
// or contaminate, the legacy service's classification.
public interface IAlertRepository
{
    // recipients is the intended notification list, snapshotted at detection time so a later
    // retry targets the same people even if the client's configured numbers change afterward.
    // Returns the persisted alert's id, used to record which recipients get confirmed notified.
    Task<string> InsertAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default);

    // Only alerts of type New/RepeatedOtherPlatform count, matching Helper.GetLastNewOrRepeatedAlert.
    Task<Alert?> GetLastNewOrRepeatedAlertAsync(string platform, string clientName, CancellationToken cancellationToken = default);

    // Merges recipients that IAlertNotifier just confirmed as sent into the alert's already-
    // notified set, so a later retry only targets whoever is still missing.
    Task MarkRecipientsNotifiedAsync(string alertId, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default);

    // Alerts whose intended recipients are not all confirmed notified yet (e.g. the process died,
    // or the WhatsApp sidecar was down, between InsertAsync and a successful send), bounded to a
    // lookback window so a very old alert is never resent.
    Task<IReadOnlyList<PendingAlertNotification>> GetPendingNotificationsAsync(TimeSpan lookback, CancellationToken cancellationToken = default);
}

public sealed record PendingAlertNotification(string AlertId, Alert Alert, IReadOnlyList<string> PendingRecipients);
