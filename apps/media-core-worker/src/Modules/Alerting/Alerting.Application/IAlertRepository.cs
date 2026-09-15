using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Equivalent to Helper.InsertAlert + Helper.GetLastNewOrRepeatedAlert
// (media-monitor/apps/w-service/Helper.cs:416-472). During the shadow-run validation period the
// implementation targets a collection separate from the legacy monitoring.alert (see
// MongoAlertingOptions.AlertCollectionName) so this repository's own dedup lookups never mix with,
// or contaminate, the legacy service's classification.
public interface IAlertRepository
{
    Task InsertAsync(Alert alert, CancellationToken cancellationToken = default);

    // Only alerts of type New/RepeatedOtherPlatform count, matching Helper.GetLastNewOrRepeatedAlert.
    Task<Alert?> GetLastNewOrRepeatedAlertAsync(string platform, string clientName, CancellationToken cancellationToken = default);
}
