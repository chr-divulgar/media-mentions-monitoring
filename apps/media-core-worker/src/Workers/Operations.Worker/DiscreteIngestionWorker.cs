using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// When the next discrete-ingestion cycle starts: on the clock (a multiple of the interval, so :00, :15, :30 and :45 for
/// 15 minutes), not an interval after the last cycle ended. Sleeping a fixed time after each cycle drifts by the length
/// of the cycle and, in the status grid, leaves some quarter of an hour with no cycle at all.
/// </summary>
internal static class DiscreteSchedule
{
    // How long to wait from now to the next mark. A cycle that overran a mark does not make up for it: the one after
    // is simply the next mark. Under a second there is nothing to align to (tests, tight loops).
    public static TimeSpan DelayUntilNextMark(DateTimeOffset now, TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1))
        {
            return interval;
        }

        return TimeSpan.FromTicks(interval.Ticks - now.UtcTicks % interval.Ticks);
    }
}

public sealed class DiscreteIngestionWorker : BackgroundService
{
    private readonly ILogger<DiscreteIngestionWorker> logger;
    private readonly OperationsWorkerOptions options;
    private readonly IDiscreteIngestionOrchestrator orchestrator;
    private readonly IOperationalMetrics operationalMetrics;

    public DiscreteIngestionWorker(
        ILogger<DiscreteIngestionWorker> logger,
        OperationsWorkerOptions options,
        IDiscreteIngestionOrchestrator orchestrator,
        IOperationalMetrics operationalMetrics)
    {
        this.logger = logger;
        this.options = options;
        this.orchestrator = orchestrator;
        this.operationalMetrics = operationalMetrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Discrete ingestion worker started with interval {DiscreteWorkerInterval}.", options.DiscreteWorkerInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await orchestrator.ExecuteCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationalMetrics.RecordCriticalError("discrete-ingestion-cycle");
                logger.LogError(exception, "Discrete ingestion cycle failed.");
            }

            try
            {
                await Task.Delay(DiscreteSchedule.DelayUntilNextMark(DateTimeOffset.UtcNow, options.DiscreteWorkerInterval), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}