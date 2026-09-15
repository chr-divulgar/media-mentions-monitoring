namespace MediaOpsCore.Modules.Alerting.Application;

public interface IDetectAlertsUseCase
{
    // Called once per recognized transcription chunk. Equivalent to
    // Monitor.processTranscription (media-monitor/apps/w-service/Monitor.cs:64-120), invoked directly
    // in-process instead of polling an unprocessed-transcription collection every 10 seconds.
    Task ExecuteAsync(
        string platform,
        string media,
        string filePath,
        string text,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default);
}
