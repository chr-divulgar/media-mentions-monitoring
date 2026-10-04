namespace MediaOpsCore.Modules.Alerting.Application;

// One recipients list carries every channel: a recipient is an email when it looks like one, and a
// WhatsApp phone number or group id otherwise. Keeping a single list is what lets the existing
// "who was already notified / retry the rest" tracking work for email unchanged.
public static class AlertRecipients
{
    // WhatsApp ids contain '@' too ("<id>@g.us" groups, "<number>@s.whatsapp.net"), so the domain
    // part decides, not the '@' alone.
    private static readonly string[] WhatsAppDomains = ["g.us", "s.whatsapp.net"];

    public static bool IsEmail(string recipient)
    {
        var value = recipient?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return false;
        }

        var domain = value[(at + 1)..];
        return domain.Contains('.')
            && !domain.StartsWith('.')
            && !domain.EndsWith('.')
            && !WhatsAppDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }
}
