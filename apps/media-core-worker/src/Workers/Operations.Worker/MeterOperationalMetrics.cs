using System.Diagnostics.Metrics;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class MeterOperationalMetrics : IOperationalMetrics, IDisposable
{
    private readonly Meter meter = new("MediaOpsCore.Workers.Operations", "1.0.0");
    private readonly Counter<int> captureAttempts;
    private readonly Counter<int> captureSuccesses;
    private readonly Counter<int> captureFailures;
    private readonly Counter<int> segmentsGenerated;
    private readonly Counter<int> processOrphanCount;
    private readonly Counter<int> reconciliationActions;
    private readonly Counter<int> criticalErrors;
    private readonly Histogram<double> pipelineLagSeconds;

    public MeterOperationalMetrics()
    {
        captureAttempts = meter.CreateCounter<int>("capture_attempt_count");
        captureSuccesses = meter.CreateCounter<int>("capture_success_count");
        captureFailures = meter.CreateCounter<int>("capture_failure_count");
        segmentsGenerated = meter.CreateCounter<int>("segment_generation_count");
        processOrphanCount = meter.CreateCounter<int>("process_orphan_count");
        reconciliationActions = meter.CreateCounter<int>("reconciliation_actions");
        criticalErrors = meter.CreateCounter<int>("critical_error_count");
        pipelineLagSeconds = meter.CreateHistogram<double>("pipeline_lag_seconds");
    }

    public void RecordCaptureRun(int attempts, int succeeded, int failed)
    {
        captureAttempts.Add(attempts);
        captureSuccesses.Add(succeeded);
        captureFailures.Add(failed);
    }

    public void RecordSegmentationRun(int segmentsGenerated, double pipelineLagSeconds)
    {
        this.segmentsGenerated.Add(segmentsGenerated);
        this.pipelineLagSeconds.Record(pipelineLagSeconds);
    }

    public void RecordProcessGuardianRun(int processOrphanCount, int reconciliationActions)
    {
        this.processOrphanCount.Add(processOrphanCount);
        this.reconciliationActions.Add(reconciliationActions);
    }

    public void RecordCriticalError(string operationName)
    {
        criticalErrors.Add(1, KeyValuePair.Create<string, object?>("operation", operationName));
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}