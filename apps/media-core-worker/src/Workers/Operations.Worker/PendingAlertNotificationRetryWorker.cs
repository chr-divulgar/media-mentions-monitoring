using MediaOpsCore.Modules.Alerting.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

// Periodic safety net alongside the one-shot startup retry in Program.cs: that one only catches a
// delivery gap that already existed when the worker started. This catches one that opens up while
// the worker is already running — e.g. the WhatsApp sidecar drops and reconnects mid-session.
public sealed class PendingAlertNotificationRetryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IDetectAlertsUseCase detectAlertsUseCase;
    private readonly ILogger<PendingAlertNotificationRetryWorker> logger;

    public PendingAlertNotificationRetryWorker(
        IDetectAlertsUseCase detectAlertsUseCase,
        ILogger<PendingAlertNotificationRetryWorker> logger)
    {
        this.detectAlertsUseCase = detectAlertsUseCase;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // WaitForNextTickAsync waits out the interval before the first pass, so this never
        // duplicates the explicit startup call in Program.cs.
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await detectAlertsUseCase.RetryPendingNotificationsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[PendingAlertNotificationRetryWorker] Retry pass failed.");
            }
        }
    }
}
