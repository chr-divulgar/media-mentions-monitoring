namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>Outbound port: an audio file as PCM s16le mono 16 kHz (<see cref="SpeechAudio"/>).</summary>
public interface IPcmFileReader
{
    /// <summary>Throws <see cref="IOException"/> when the file cannot be read, <see cref="InvalidDataException"/> when it is not audio in that format.</summary>
    Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>The text of one audio file. Status is the label of <see cref="SpeechRecognitionStatus"/> (see ToLabel).</summary>
public sealed record TranscriptionChunkResult(string Status, string Text);

/// <summary>The words a status is written with in the transcription JSON and in answers to the portal.</summary>
public static class SpeechRecognitionStatusLabels
{
    public static string ToLabel(this SpeechRecognitionStatus status) => status switch
    {
        SpeechRecognitionStatus.Recognized => "recognized",
        SpeechRecognitionStatus.NoSpeech => "no_speech",
        SpeechRecognitionStatus.InvalidAudio => "audio_error",
        SpeechRecognitionStatus.RateLimited => "rate_limited",
        SpeechRecognitionStatus.HttpError => "api_error",
        SpeechRecognitionStatus.Timeout => "timeout",
        SpeechRecognitionStatus.NetworkError => "network_error",
        _ => "unknown",
    };
}

/// <summary>
/// The portal's on-demand transcription (the note editor's "transcribe"): the text of each audio file, through
/// whatever engine sits behind <see cref="ISpeechToText"/>. At most <see cref="MaxConcurrency"/> files are asked at
/// once (the old way asked all of them together and tripped the engine's rate limit), a transient failure is asked
/// again with a growing wait, and a file that cannot be read is a result for that file, never a failure of the rest.
/// </summary>
public sealed class TranscribeAudioFilesUseCase
{
    public const int MaxConcurrency = 3;

    // The waits before each retry: three retries, so four attempts in all.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)];

    private readonly ISpeechToText speechToText;
    private readonly IPcmFileReader fileReader;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public TranscribeAudioFilesUseCase(
        ISpeechToText speechToText, IPcmFileReader fileReader, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.speechToText = speechToText;
        this.fileReader = fileReader;
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>One result per path, in the order of the paths.</summary>
    public async Task<IReadOnlyList<TranscriptionChunkResult>> TranscribeAsync(
        IReadOnlyList<string> audioPaths, string language, CancellationToken cancellationToken = default)
    {
        var results = new TranscriptionChunkResult[audioPaths.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, audioPaths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await TranscribeOneAsync(audioPaths[index], language, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    private async Task<TranscriptionChunkResult> TranscribeOneAsync(string path, string language, CancellationToken cancellationToken)
    {
        byte[] pcm;
        try
        {
            pcm = await fileReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new TranscriptionChunkResult(SpeechRecognitionStatus.InvalidAudio.ToLabel(), string.Empty);
        }

        for (var attempt = 0; ; attempt++)
        {
            var result = await speechToText.RecognizeAsync(pcm, language, cancellationToken).ConfigureAwait(false);
            if (!IsTransient(result.Status) || attempt >= RetryDelays.Length)
            {
                return new TranscriptionChunkResult(
                    result.Status.ToLabel(), result.Status == SpeechRecognitionStatus.Recognized ? result.Text : string.Empty);
            }

            await delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    // Worth asking again: the engine or the network, not the audio (silence and invalid audio will not change).
    private static bool IsTransient(SpeechRecognitionStatus status) =>
        status is SpeechRecognitionStatus.RateLimited or SpeechRecognitionStatus.HttpError
            or SpeechRecognitionStatus.Timeout or SpeechRecognitionStatus.NetworkError;
}
