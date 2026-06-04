namespace MediaOpsCore.BuildingBlocks.Application;

public interface IOperationalMetrics
{
    void RecordCaptureRun(int attempts, int succeeded, int failed);

    void RecordSegmentationRun(int segmentsGenerated, double pipelineLagSeconds);

    void RecordCriticalError(string operationName);
}