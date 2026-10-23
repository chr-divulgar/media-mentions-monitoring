using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Polls the WhatsApp sidecar's /status and, when it stays out of "connected" (logged out and
/// waiting for a QR, or unreachable), emails the opted-in users — WhatsApp cannot carry that news.
/// Once per outage; a short grace period absorbs the normal startup pairing and quick reconnects.
/// </summary>
public sealed class WhatsAppConnectionWatchdog : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(3);

    // /status answers in milliseconds when the sidecar is alive: waiting longer is just a slower way to learn it is not
    // (the shared HttpClient's own limit is 100 s).
    private static readonly TimeSpan DefaultStatusTimeout = TimeSpan.FromSeconds(5);

    private readonly OperationsWorkerOptions options;
    private readonly HttpClient httpClient;
    private readonly IPlatformErrorNotifier notifier;
    private readonly ILogger<WhatsAppConnectionWatchdog> logger;
    private readonly TimeSpan statusTimeout;
    private DateTimeOffset? disconnectedSince;
    private bool alerted;

    public WhatsAppConnectionWatchdog(
        OperationsWorkerOptions options,
        HttpClient httpClient,
        IPlatformErrorNotifier notifier,
        ILogger<WhatsAppConnectionWatchdog> logger,
        TimeSpan? statusTimeout = null)
    {
        this.statusTimeout = statusTimeout ?? DefaultStatusTimeout;
        this.options = options;
        this.httpClient = httpClient;
        this.notifier = notifier;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                if (Observe(await IsConnectedAsync(stoppingToken).ConfigureAwait(false), DateTimeOffset.UtcNow))
                {
                    logger.LogWarning("[WhatsAppConnectionWatchdog] WhatsApp has been disconnected for over {Minutes} min; emailing users.", GracePeriod.TotalMinutes);
                    await notifier.NotifyAsync(
                        "⚠️ Error de la plataforma: WhatsApp se desconectó (sesión cerrada). " +
                        "Los avisos por WhatsApp no se están enviando. Hay que volver a vincular el número escaneando el código QR " +
                        "en Configuración → WhatsApp.",
                        "Error de plataforma: WhatsApp desconectado",
                        viaWhatsApp: false,
                        stoppingToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // True exactly once per outage: the first observation after the grace period has elapsed.
    internal bool Observe(bool connected, DateTimeOffset now)
    {
        if (connected)
        {
            disconnectedSince = null;
            alerted = false;
            return false;
        }

        disconnectedSince ??= now;
        if (alerted || now - disconnectedSince < GracePeriod)
        {
            return false;
        }

        alerted = true;
        return true;
    }

    internal async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The watchdog's own limit; a stop of the worker still cancels through the same token.
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limited.CancelAfter(statusTimeout);

            using var response = await httpClient
                .GetAsync($"http://localhost:{options.WhatsAppSidecarPort}/status", limited.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var json = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(limited.Token).ConfigureAwait(false), cancellationToken: limited.Token)
                .ConfigureAwait(false);
            return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "connected";
        }
        // Not "is not OperationCanceledException": the HttpClient's own timeout is a TaskCanceledException, and letting it
        // out of a BackgroundService stops the whole host (and the capture with it). Only a real shutdown passes through.
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("[WhatsAppConnectionWatchdog] Sidecar /status unreachable.");
            return false;
        }
    }
}
