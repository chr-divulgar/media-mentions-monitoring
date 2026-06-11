namespace MediaOpsCore.Modules.Capture.Application;

public sealed class AudioCaptureExecutionResult
{
    public AudioCaptureExecutionResult(
        bool succeeded,
        string opusFilePath,
        string? errorMessage = null,
        double silenceFilledSeconds = 0)
    {
        Succeeded = succeeded;
        OpusFilePath = opusFilePath;
        ErrorMessage = errorMessage;
        SilenceFilledSeconds = silenceFilledSeconds >= 0 ? silenceFilledSeconds : 0;
    }

    public bool Succeeded { get; }

    public string OpusFilePath { get; }

    public string? ErrorMessage { get; }

    /// <summary>
    /// Seconds of silence injected into the current rotation window's opus file to
    /// bridge a recording gap (mid-hour resume or previous-window fill).
    /// Zero when the source has been recording without interruption.
    /// </summary>
    public double SilenceFilledSeconds { get; }
}

