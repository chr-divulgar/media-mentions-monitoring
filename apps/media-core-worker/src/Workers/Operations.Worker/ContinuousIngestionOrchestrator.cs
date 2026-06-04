using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using MediaOpsCore.Modules.Segmentation.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class ContinuousIngestionOrchestrator : IContinuousIngestionOrchestrator
{
    private readonly ILogger<ContinuousIngestionOrchestrator> logger;
    private readonly IContinuousCaptureUseCase continuousCaptureUseCase;
    private readonly IIncrementalSegmentationUseCase incrementalSegmentationUseCase;
    private readonly IProcessMonitorUseCase processMonitorUseCase;
    private readonly IReconcileInactiveUseCase reconcileInactiveUseCase;
    private readonly IChunkProcessMonitorUseCase chunkProcessMonitorUseCase;
    private readonly IOperationalMetrics operationalMetrics;

    public ContinuousIngestionOrchestrator(
        ILogger<ContinuousIngestionOrchestrator> logger,
        IContinuousCaptureUseCase continuousCaptureUseCase,
        IIncrementalSegmentationUseCase incrementalSegmentationUseCase,
        IProcessMonitorUseCase processMonitorUseCase,
        IReconcileInactiveUseCase reconcileInactiveUseCase,
        IChunkProcessMonitorUseCase chunkProcessMonitorUseCase,
        IOperationalMetrics operationalMetrics)
    {
        this.logger = logger;
        this.continuousCaptureUseCase = continuousCaptureUseCase;
        this.incrementalSegmentationUseCase = incrementalSegmentationUseCase;
        this.processMonitorUseCase = processMonitorUseCase;
        this.reconcileInactiveUseCase = reconcileInactiveUseCase;
        this.chunkProcessMonitorUseCase = chunkProcessMonitorUseCase;
        this.operationalMetrics = operationalMetrics;
    }

    public async Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
    {
        var captureResult = await continuousCaptureUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        operationalMetrics.RecordCaptureRun(captureResult.Attempts, captureResult.Succeeded, captureResult.Failed);

        var segmentationResult = await incrementalSegmentationUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        operationalMetrics.RecordSegmentationRun(segmentationResult.SegmentsGenerated, segmentationResult.PipelineLagSeconds);

        var processMonitorResult = await processMonitorUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var reconcileInactiveResult = await reconcileInactiveUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var chunkMonitorResult = await chunkProcessMonitorUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        operationalMetrics.RecordProcessGuardianRun(chunkMonitorResult.OrphansDetected, reconcileInactiveResult.Reconciled);

        logger.LogInformation(
            "Continuous ingestion cycle completed. capture_attempts={CaptureAttempts} capture_succeeded={CaptureSucceeded} capture_failed={CaptureFailed} segments_generated={SegmentsGenerated} pipeline_lag_seconds={PipelineLagSeconds} process_inspected={ProcessInspected} process_restarted={ProcessRestarted} process_timed_out={ProcessTimedOut} reconciled_inactive={ReconciledInactive} orphans_detected={OrphansDetected} orphans_stopped={OrphansStopped}.",
            captureResult.Attempts,
            captureResult.Succeeded,
            captureResult.Failed,
            segmentationResult.SegmentsGenerated,
            segmentationResult.PipelineLagSeconds,
            processMonitorResult.Inspected,
            processMonitorResult.Restarted,
            processMonitorResult.TimedOut,
            reconcileInactiveResult.Reconciled,
            chunkMonitorResult.OrphansDetected,
            chunkMonitorResult.OrphansStopped);
    }
}