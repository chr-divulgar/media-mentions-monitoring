using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

public sealed record AlertEmail(string Subject, string Body);

// The email form of an alert: the same content as the WhatsApp message with its *bold* markers
// removed (email shows them literally), and the level in the subject where a mail client lists it.
public static class AlertEmailMessage
{
    public static AlertEmail Build(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var level = alert.Level == AlertLevel.Informative ? string.Empty : $"[{alert.Level.ToLabel()}] ";
        var subject = $"{level}{alert.ClientName} — {alert.Platform}";
        var body = (AlertMessageFormatter.Format(alert) ?? string.Empty).Replace("*", string.Empty);

        return new AlertEmail(subject, body);
    }
}
