using System.Net.Http.Json;
using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Modules.Alerting.Infrastructure;

// Calls the Node.js/Baileys sidecar's POST /send directly (WhatsAppSidecarProcessSupervisor in
// Operations.Worker owns starting/restarting that sidecar). Best-effort per the IAlertNotifier
// contract: every failure is logged and swallowed here, never rethrown — a client's WhatsApp
// number being unreachable, or the sidecar itself being mid-restart, must not affect alert
// detection/persistence for anyone else.
public sealed class HttpWhatsAppAlertNotifier : IAlertNotifier
{
    private readonly HttpClient httpClient;
    private readonly WhatsAppSidecarOptions options;
    private readonly ILogger<HttpWhatsAppAlertNotifier> logger;

    public HttpWhatsAppAlertNotifier(HttpClient httpClient, WhatsAppSidecarOptions options, ILogger<HttpWhatsAppAlertNotifier> logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var message = AlertMessageFormatter.Format(alert);
        if (message is null || recipients.Count == 0)
        {
            return Array.Empty<string>();
        }

        var succeeded = new List<string>(recipients.Count);
        foreach (var number in recipients)
        {
            if (await SendOneAsync(number, message, cancellationToken).ConfigureAwait(false))
            {
                succeeded.Add(number);
            }
        }

        return succeeded;
    }

    private async Task<bool> SendOneAsync(string number, string message, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

            using var response = await httpClient
                .PostAsJsonAsync($"{options.BaseUrl.TrimEnd('/')}/send", new { number, message }, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "[HttpWhatsAppAlertNotifier] Sidecar rejected message to {Number}: {StatusCode}",
                    number,
                    response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[HttpWhatsAppAlertNotifier] Timed out sending WhatsApp message to {Number}.", number);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[HttpWhatsAppAlertNotifier] Failed to send WhatsApp message to {Number}.", number);
            return false;
        }
    }
}
