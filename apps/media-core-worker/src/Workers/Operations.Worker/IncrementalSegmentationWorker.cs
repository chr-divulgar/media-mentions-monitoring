using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Segmentation.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class IncrementalSegmentationWorker : BackgroundService
{
    private readonly IIncrementalSegmentationUseCase segmentationUseCase;
    private readonly IOperationalMetrics operationalMetrics;
    private readonly OperationsWorkerOptions options;
    private readonly ILogger<IncrementalSegmentationWorker> logger;

    public IncrementalSegmentationWorker(
        IIncrementalSegmentationUseCase segmentationUseCase,
        IOperationalMetrics operationalMetrics,
        OperationsWorkerOptions options,
        ILogger<IncrementalSegmentationWorker> logger)
    {
        this.segmentationUseCase = segmentationUseCase;
        this.operationalMetrics = operationalMetrics;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Incremental segmentation worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await segmentationUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                operationalMetrics.RecordSegmentationRun(result.SegmentsGenerated, result.PipelineLagSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Segmentation cycle failed.");
            }

            try
            {
                await Task.Delay(options.SegmentationInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
