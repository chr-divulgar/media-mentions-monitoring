using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public interface ICaptureAttemptObserver
{
    Task ReportAsync(CaptureSource source, AudioCaptureExecutionResult result, CancellationToken cancellationToken = default);
}

public sealed class NullCaptureAttemptObserver : ICaptureAttemptObserver
{
    public static readonly NullCaptureAttemptObserver Instance = new();

    private NullCaptureAttemptObserver()
    {
    }

    public Task ReportAsync(CaptureSource source, AudioCaptureExecutionResult result, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
