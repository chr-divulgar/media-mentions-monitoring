using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// One IAlertNotifier for every channel: email recipients go to the email notifier, everything else
// (phone numbers, WhatsApp group ids) to WhatsApp. With no email channel configured, emails are
// skipped — never handed to WhatsApp, which would try to text an address.
public sealed class RoutingAlertNotifier : IAlertNotifier
{
    private readonly IAlertNotifier whatsApp;
    private readonly IAlertNotifier? email;
    private readonly ILogger<RoutingAlertNotifier> logger;

    public RoutingAlertNotifier(IAlertNotifier whatsApp, IAlertNotifier? email, ILogger<RoutingAlertNotifier> logger)
    {
        this.whatsApp = whatsApp ?? throw new ArgumentNullException(nameof(whatsApp));
        this.email = email;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        var emails = recipients.Where(AlertRecipients.IsEmail).ToArray();
        var others = recipients.Where(recipient => !AlertRecipients.IsEmail(recipient)).ToArray();

        var succeeded = new List<string>(recipients.Count);
        if (others.Length > 0)
        {
            succeeded.AddRange(await whatsApp.NotifyAsync(alert, others, cancellationToken).ConfigureAwait(false));
        }

        if (emails.Length > 0)
        {
            if (email is null)
            {
                logger.LogWarning("[RoutingAlertNotifier] {Count} email recipient(s) skipped: no SMTP configured (SMTP_HOST / SMTP_FROM).", emails.Length);
            }
            else
            {
                succeeded.AddRange(await email.NotifyAsync(alert, emails, cancellationToken).ConfigureAwait(false));
            }
        }

        return succeeded;
    }
}
