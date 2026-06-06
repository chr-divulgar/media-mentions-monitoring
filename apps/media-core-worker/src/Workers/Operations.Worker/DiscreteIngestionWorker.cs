using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

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
                await Task.Delay(options.DiscreteWorkerInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}