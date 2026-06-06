namespace MediaOpsCore.Modules.Capture.Application;

public sealed class AudioCaptureExecutionResult
{
    public AudioCaptureExecutionResult(
        bool succeeded,
        string opusFilePath,
        string? errorMessage = null)
    {
        Succeeded = succeeded;
        OpusFilePath = opusFilePath;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    public string OpusFilePath { get; }

    public string? ErrorMessage { get; }
}

