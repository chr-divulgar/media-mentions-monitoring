namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>How a recognition call ended. Anything but Recognized and NoSpeech is a failure, never silence.</summary>
public enum SpeechRecognitionStatus
{
    Recognized,
    NoSpeech,
    InvalidAudio,
    RateLimited,
    HttpError,
    Timeout,
    NetworkError,
}

public readonly record struct SpeechRecognitionResult(SpeechRecognitionStatus Status, string Text);

/// <summary>The one audio format every engine is given: PCM signed 16-bit little-endian, mono, 16 kHz.</summary>
public static class SpeechAudio
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BytesPerSample = 2;
}

/// <summary>
/// Outbound port of the speech recognition engine. Capture and the portal's on-demand transcription both ask
/// through it, so changing the engine (a free endpoint, a licensed cloud service, a local model) is a new adapter and
/// one line of configuration, never a change to the pipeline. Each engine converts the PCM to what it needs.
/// </summary>
public interface ISpeechToText
{
    /// <summary>
    /// Recognizes one window of audio. Never throws for a failed call: the outcome is the status. It throws only
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken = default);
}
