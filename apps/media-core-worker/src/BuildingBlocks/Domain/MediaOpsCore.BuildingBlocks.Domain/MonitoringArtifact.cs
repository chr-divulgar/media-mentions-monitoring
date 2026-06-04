namespace MediaOpsCore.BuildingBlocks.Domain;

public sealed class MonitoringArtifact
{
    public MonitoringArtifact(
        string id,
        string tenantId,
        string source,
        string kind,
        string payloadJson,
        DateTimeOffset capturedAtUtc)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(id)) : id;
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(tenantId)) : tenantId;
        Source = string.IsNullOrWhiteSpace(source) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(source)) : source;
        Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(kind)) : kind;
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(payloadJson)) : payloadJson;
        CapturedAtUtc = capturedAtUtc;
    }

    public string Id { get; }

    public string TenantId { get; }

    public string Source { get; }

    public string Kind { get; }

    public string PayloadJson { get; }

    public DateTimeOffset CapturedAtUtc { get; }
}