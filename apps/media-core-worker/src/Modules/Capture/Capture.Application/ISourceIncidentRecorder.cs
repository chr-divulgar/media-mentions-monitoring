namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Keeps the "why" behind a gap in a source's recording: the failure, every retry and the
/// recovery. Capture status only knows a stretch has no audio; this is what explains it.
/// </summary>
public interface ISourceIncidentRecorder
{
    /// <summary>Must never throw: it is called from the capture and recovery paths.</summary>
    void Record(string sourceId, string kind, string? reason = null);
}

public static class IncidentKinds
{
    public const string Failed = "failed";
    public const string RetryFailed = "retry-failed";
    public const string Recovered = "recovered";
    public const string Excluded = "excluded";
    public const string StartupFailed = "startup-failed";
    public const string WorkerStarted = "worker-started";
    public const string WorkerStopped = "worker-stopped";

    // RetentionPurgeWorker, recorded under WorkerSourceId — low disk space is a worker-wide
    // condition, not any one source's problem.
    public const string DiskLow = "disk-low";
    public const string DiskRecovered = "disk-recovered";
    public const string PurgeFailed = "purge-failed";

    // WebNewsCaptureUseCase, recorded under SourceId "google-news" (see that class).
    public const string CycleOk = "cycle-ok";
    public const string CycleFailed = "cycle-failed";
    public const string ArticleAlerted = "article-alerted";
    public const string ArticleRejected = "article-rejected";
    public const string ArticleFailed = "article-failed";
    public const string SendFailed = "send-failed";

    /// <summary>Source id for events that concern the whole worker, not one source.</summary>
    public const string WorkerSourceId = "*";
}

public sealed class NullSourceIncidentRecorder : ISourceIncidentRecorder
{
    public static readonly NullSourceIncidentRecorder Instance = new();

    private NullSourceIncidentRecorder()
    {
    }

    public void Record(string sourceId, string kind, string? reason = null)
    {
    }
}
