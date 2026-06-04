using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class ContinuousIngestionWorker : BackgroundService
{
    private readonly ILogger<ContinuousIngestionWorker> logger;
    private readonly OperationsWorkerOptions options;
    private readonly IContinuousIngestionOrchestrator orchestrator;
    private readonly IOperationalMetrics operationalMetrics;

    public ContinuousIngestionWorker(
        ILogger<ContinuousIngestionWorker> logger,
        OperationsWorkerOptions options,
        IContinuousIngestionOrchestrator orchestrator,
        IOperationalMetrics operationalMetrics)
    {
        this.logger = logger;
        this.options = options;
        this.orchestrator = orchestrator;
        this.operationalMetrics = operationalMetrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Continuous ingestion worker started with heartbeat interval {HeartbeatInterval}.", options.HeartbeatInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await orchestrator.ExecuteCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationalMetrics.RecordCriticalError("continuous-ingestion-cycle");
                logger.LogError(exception, "Continuous ingestion cycle failed.");
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }
}