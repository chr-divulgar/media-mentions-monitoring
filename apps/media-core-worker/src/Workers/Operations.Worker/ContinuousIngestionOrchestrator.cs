using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Segmentation.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class ContinuousIngestionOrchestrator : IContinuousIngestionOrchestrator
{
    private readonly ILogger<ContinuousIngestionOrchestrator> logger;
    private readonly IContinuousCaptureUseCase continuousCaptureUseCase;
    private readonly IIncrementalSegmentationUseCase incrementalSegmentationUseCase;
    private readonly IOperationalMetrics operationalMetrics;

    public ContinuousIngestionOrchestrator(
        ILogger<ContinuousIngestionOrchestrator> logger,
        IContinuousCaptureUseCase continuousCaptureUseCase,
        IIncrementalSegmentationUseCase incrementalSegmentationUseCase,
        IOperationalMetrics operationalMetrics)
    {
        this.logger = logger;
        this.continuousCaptureUseCase = continuousCaptureUseCase;
        this.incrementalSegmentationUseCase = incrementalSegmentationUseCase;
        this.operationalMetrics = operationalMetrics;
    }

    public async Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
    {
        var captureResult = await continuousCaptureUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        operationalMetrics.RecordCaptureRun(captureResult.Attempts, captureResult.Succeeded, captureResult.Failed);

        var segmentationResult = await incrementalSegmentationUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        operationalMetrics.RecordSegmentationRun(segmentationResult.SegmentsGenerated, segmentationResult.PipelineLagSeconds);

        logger.LogInformation(
            "Continuous ingestion cycle completed. capture_attempts={CaptureAttempts} capture_succeeded={CaptureSucceeded} capture_failed={CaptureFailed} segments_generated={SegmentsGenerated} pipeline_lag_seconds={PipelineLagSeconds}.",
            captureResult.Attempts,
            captureResult.Succeeded,
            captureResult.Failed,
            segmentationResult.SegmentsGenerated,
            segmentationResult.PipelineLagSeconds);
    }
}
