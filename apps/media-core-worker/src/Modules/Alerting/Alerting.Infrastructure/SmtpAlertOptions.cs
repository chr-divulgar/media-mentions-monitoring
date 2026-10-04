namespace MediaOpsCore.Modules.Alerting.Infrastructure;

public sealed class SmtpAlertOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = string.Empty;

    public bool EnableSsl { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;
}
