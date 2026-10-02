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
