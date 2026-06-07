namespace MediaOpsCore.BuildingBlocks.Application;

public interface IOperationalMetrics
{
    void RecordCaptureRun(int attempts, int succeeded, int failed);

    void RecordCaptureRuntimeFailure(string sourceId);

    void RecordSegmentationRun(int segmentsGenerated, double pipelineLagSeconds);

    void RecordProcessGuardianRun(int processOrphanCount, int reconciliationActions);

    void RecordCriticalError(string operationName);
}