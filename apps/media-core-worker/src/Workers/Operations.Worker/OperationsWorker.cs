using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using MediaOpsCore.Modules.Segmentation.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorker : BackgroundService
{
    private readonly ILogger<OperationsWorker> logger;
    private readonly OperationsWorkerOptions options;
    private readonly IContinuousCaptureUseCase continuousCaptureUseCase;
    private readonly IIncrementalSegmentationUseCase incrementalSegmentationUseCase;
    private readonly IProcessMonitorUseCase processMonitorUseCase;
    private readonly IReconcileInactiveUseCase reconcileInactiveUseCase;
    private readonly IChunkProcessMonitorUseCase chunkProcessMonitorUseCase;
    private readonly IFunctionalParityUseCase functionalParityUseCase;
    private readonly IEvidenceFileStore evidenceFileStore;
    private readonly CanaryRolloutTuner canaryRolloutTuner;
    private readonly IOperationalMetrics operationalMetrics;

    public OperationsWorker(
        ILogger<OperationsWorker> logger,
        OperationsWorkerOptions options,
        IContinuousCaptureUseCase continuousCaptureUseCase,
        IIncrementalSegmentationUseCase incrementalSegmentationUseCase,
        IProcessMonitorUseCase processMonitorUseCase,
        IReconcileInactiveUseCase reconcileInactiveUseCase,
        IChunkProcessMonitorUseCase chunkProcessMonitorUseCase,
        IFunctionalParityUseCase functionalParityUseCase,
        IEvidenceFileStore evidenceFileStore,
        CanaryRolloutTuner canaryRolloutTuner,
        IOperationalMetrics operationalMetrics)
    {
        this.logger = logger;
        this.options = options;
        this.continuousCaptureUseCase = continuousCaptureUseCase;
        this.incrementalSegmentationUseCase = incrementalSegmentationUseCase;
        this.processMonitorUseCase = processMonitorUseCase;
        this.reconcileInactiveUseCase = reconcileInactiveUseCase;
        this.chunkProcessMonitorUseCase = chunkProcessMonitorUseCase;
        this.functionalParityUseCase = functionalParityUseCase;
        this.evidenceFileStore = evidenceFileStore;
        this.canaryRolloutTuner = canaryRolloutTuner;
        this.operationalMetrics = operationalMetrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operations worker started with heartbeat interval {HeartbeatInterval}.", options.HeartbeatInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var captureResult = await continuousCaptureUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                operationalMetrics.RecordCaptureRun(captureResult.Attempts, captureResult.Succeeded, captureResult.Failed);

                var segmentationResult = await incrementalSegmentationUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                operationalMetrics.RecordSegmentationRun(segmentationResult.SegmentsGenerated, segmentationResult.PipelineLagSeconds);

                var processMonitorResult = await processMonitorUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                var reconcileInactiveResult = await reconcileInactiveUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                var chunkMonitorResult = await chunkProcessMonitorUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                operationalMetrics.RecordProcessGuardianRun(chunkMonitorResult.OrphansDetected, reconcileInactiveResult.Reconciled);

                if (options.EnableShadowMode)
                {
                    var parityReport = await functionalParityUseCase.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                    var fileName = $"shadow/parity-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
                    await evidenceFileStore.WriteJsonAsync(fileName, parityReport, stoppingToken).ConfigureAwait(false);

                    var tuningDecision = canaryRolloutTuner.Apply(parityReport);
                    var tuningFile = $"shadow/canary-tuning-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
                    await evidenceFileStore.WriteJsonAsync(tuningFile, tuningDecision, stoppingToken).ConfigureAwait(false);

                    logger.LogInformation(
                        "Canary tuning applied. previous_percent={PreviousPercent} current_percent={CurrentPercent} increased={Increased} decreased={Decreased} reason={Reason}.",
                        tuningDecision.PreviousPercent,
                        tuningDecision.CurrentPercent,
                        tuningDecision.Increased,
                        tuningDecision.Decreased,
                        tuningDecision.Reason);

                    if (!parityReport.MeetsThreshold)
                    {
                        logger.LogWarning(
                            "Shadow parity threshold not met. overall_parity={OverallParityPercent} minimum={MinimumParityPercent}.",
                            parityReport.OverallParityPercent,
                            options.ShadowParityMinimumPercent);
                    }
                }

                logger.LogInformation(
                    "Operations cycle completed. capture_attempts={CaptureAttempts} capture_succeeded={CaptureSucceeded} capture_failed={CaptureFailed} segments_generated={SegmentsGenerated} pipeline_lag_seconds={PipelineLagSeconds} process_inspected={ProcessInspected} process_restarted={ProcessRestarted} process_timed_out={ProcessTimedOut} reconciled_inactive={ReconciledInactive} orphans_detected={OrphansDetected} orphans_stopped={OrphansStopped}.",
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
            catch (Exception exception)
            {
                operationalMetrics.RecordCriticalError("operations-cycle");
                logger.LogError(exception, "Operations cycle failed.");
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }
}