using System.Net;
using System.Net.Mail;
using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// Sends the alert as a plain-text email over SMTP. Best-effort per the IAlertNotifier contract: a
// bad address or a down mail server is logged and reported as "not delivered" (so it is retried),
// never thrown into alert detection.
public sealed class SmtpAlertEmailNotifier : IAlertNotifier
{
    private readonly SmtpAlertOptions options;
    private readonly ILogger<SmtpAlertEmailNotifier> logger;

    public SmtpAlertEmailNotifier(SmtpAlertOptions options, ILogger<SmtpAlertEmailNotifier> logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var email = AlertEmailMessage.Build(alert);
        if (string.IsNullOrEmpty(email.Body) || recipients.Count == 0)
        {
            return Array.Empty<string>();
        }

        var succeeded = new List<string>(recipients.Count);
        foreach (var recipient in recipients)
        {
            if (await SendOneAsync(recipient, email, cancellationToken).ConfigureAwait(false))
            {
                succeeded.Add(recipient);
            }
        }

        return succeeded;
    }

    private async Task<bool> SendOneAsync(string recipient, AlertEmail email, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.EnableSsl,
                Timeout = options.TimeoutSeconds * 1000,
                Credentials = string.IsNullOrWhiteSpace(options.User) ? null : new NetworkCredential(options.User, options.Password),
            };
            using var message = new MailMessage(options.From, recipient.Trim(), email.Subject, email.Body);

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[SmtpAlertEmailNotifier] Failed to email {Recipient}.", recipient);
            return false;
        }
    }
}
