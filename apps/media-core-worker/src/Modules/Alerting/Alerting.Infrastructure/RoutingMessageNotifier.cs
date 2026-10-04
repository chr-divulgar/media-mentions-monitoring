using MediaOpsCore.Modules.Alerting.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// Same routing rule as RoutingAlertNotifier, for free-form text: emails over SMTP when it is
// configured (skipped otherwise), everything else to WhatsApp.
public sealed class RoutingMessageNotifier : IMessageNotifier
{
    private readonly IMessageNotifier whatsApp;
    private readonly IMessageNotifier? email;
    private readonly ILogger<RoutingMessageNotifier> logger;

    public RoutingMessageNotifier(IMessageNotifier whatsApp, IMessageNotifier? email, ILogger<RoutingMessageNotifier> logger)
    {
        this.whatsApp = whatsApp ?? throw new ArgumentNullException(nameof(whatsApp));
        this.email = email;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<string>> SendAsync(string text, IReadOnlyList<string> recipients, string? subject = null, CancellationToken cancellationToken = default)
    {
        var emails = recipients.Where(AlertRecipients.IsEmail).ToArray();
        var others = recipients.Where(recipient => !AlertRecipients.IsEmail(recipient)).ToArray();

        var succeeded = new List<string>(recipients.Count);
        if (others.Length > 0)
        {
            succeeded.AddRange(await whatsApp.SendAsync(text, others, subject, cancellationToken).ConfigureAwait(false));
        }

        if (emails.Length > 0)
        {
            if (email is null)
            {
                logger.LogWarning("[RoutingMessageNotifier] {Count} email recipient(s) skipped: no SMTP configured.", emails.Length);
            }
            else
            {
                succeeded.AddRange(await email.SendAsync(text, emails, subject, cancellationToken).ConfigureAwait(false));
            }
        }

        return succeeded;
    }
}
