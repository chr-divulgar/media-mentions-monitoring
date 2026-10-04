using MediaOpsCore.Modules.Alerting.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

// Wakes every minute and sends the day's headlines digest once, from the configured Bogota time
// onwards. "Once" is guaranteed by IDailyDigestLog, not by this loop: a restart at 07:05 sees the
// day already marked and does nothing, and a worker that was down at 07:00 still sends at 07:20.
public sealed class DailyHeadlinesWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IDailyHeadlinesDigestUseCase digestUseCase;
    private readonly IDailyDigestLog digestLog;
    private readonly TimeOnly sendTime;
    private readonly Func<DateTimeOffset> clock;
    private readonly ILogger<DailyHeadlinesWorker> logger;

    public DailyHeadlinesWorker(
        IDailyHeadlinesDigestUseCase digestUseCase,
        IDailyDigestLog digestLog,
        TimeOnly sendTime,
        ILogger<DailyHeadlinesWorker> logger,
        Func<DateTimeOffset>? clock = null)
    {
        this.digestUseCase = digestUseCase;
        this.digestLog = digestLog;
        this.sendTime = sendTime;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[DailyHeadlinesWorker] Digest pass failed; will retry next minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // Internal so a test can drive one tick without waiting on the timer.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock();
        var day = DailyHeadlinesSchedule.BogotaDay(now);
        var alreadySent = await digestLog.WasSentAsync(day, cancellationToken).ConfigureAwait(false);

        if (!DailyHeadlinesSchedule.IsDue(now, sendTime, alreadySent))
        {
            return;
        }

        var notified = await digestUseCase.ExecuteAsync(day, cancellationToken).ConfigureAwait(false);
        await digestLog.MarkSentAsync(day, notified, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("[DailyHeadlinesWorker] Daily headlines for {Day} sent to {Count} client(s).", day, notified);
    }
}
