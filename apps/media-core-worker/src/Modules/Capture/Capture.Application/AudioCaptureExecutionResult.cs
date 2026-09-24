namespace MediaOpsCore.Modules.Capture.Application;

public sealed class AudioCaptureExecutionResult
{
    public AudioCaptureExecutionResult(
        bool succeeded,
        string opusFilePath,
        string? errorMessage = null,
        double silenceFilledSeconds = 0,
        double capturedSeconds = 0,
        bool excludeSource = false)
    {
        ExcludeSource = excludeSource;
        Succeeded = succeeded;
        OpusFilePath = opusFilePath;
        ErrorMessage = errorMessage;
        SilenceFilledSeconds = silenceFilledSeconds >= 0 ? silenceFilledSeconds : 0;
        CapturedSeconds = capturedSeconds >= 0 ? capturedSeconds : 0;
    }

    public bool Succeeded { get; }

    public string OpusFilePath { get; }

    public string? ErrorMessage { get; }

    /// <summary>
    /// The stream is reachable but its content is unusable (e.g. it keeps replaying the same
    /// audio), so retrying it immediately would only record the same thing again.
    /// </summary>
    public bool ExcludeSource { get; }

    /// <summary>
    /// Real audio seconds captured this rotation window (excludes silence fills).
    /// Derived from actual encoded samples — accurate to the frame level.
    /// </summary>
    public double CapturedSeconds { get; }

    /// <summary>
    /// Seconds of silence injected into the current rotation window's opus file to
    /// bridge a recording gap (mid-hour resume or previous-window fill).
    /// </summary>
    public double SilenceFilledSeconds { get; }
}

