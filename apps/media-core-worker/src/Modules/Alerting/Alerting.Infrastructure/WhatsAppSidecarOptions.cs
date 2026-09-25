namespace MediaOpsCore.Modules.Alerting.Infrastructure;

public sealed class WhatsAppSidecarOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5101";

    public int RequestTimeoutSeconds { get; set; } = 15;
}
