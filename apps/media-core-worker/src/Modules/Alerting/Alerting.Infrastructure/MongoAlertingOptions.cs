namespace MediaOpsCore.Modules.Alerting.Infrastructure;

public sealed class MongoAlertingOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    public string ConfigDatabaseName { get; set; } = "config";

    public string MonitoringDatabaseName { get; set; } = "monitoring";

    // "workerAlert" during the shadow-run validation period; switches to "alert" at cutover
    // (see the plan's "Plan de corte") — a config change, not a code change.
    public string AlertCollectionName { get; set; } = "workerAlert";
}
