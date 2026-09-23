namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Serves an already-closed (rotated) slice of a source's recording, transcoded to the format the
/// caller needs — the counterpart to ILiveCaptureProgressReader for the in-progress hour. Reading
/// the still-open current hour is unsafe (see OpusSegmentTranscoder's doc comment) and out of
/// scope here; a request that touches it gets ClosedHourAudioStillRecording instead.
/// </summary>
public interface IClosedHourAudioReader
{
    Task<ClosedHourAudioResult> ExtractSegmentAsync(
        string sourceId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bitrateKbps,
        int frequencyHz,
        string format,
        CancellationToken cancellationToken = default);
}

public abstract record ClosedHourAudioResult;

public sealed record ClosedHourAudioSuccess(byte[] Bytes, string ContentType) : ClosedHourAudioResult;

public sealed record ClosedHourAudioSourceNotFound : ClosedHourAudioResult;

/// <param name="RecordedSeconds">
/// How far the live session has actually recorded into the current hour, per
/// ILiveCaptureProgressReader — 0 if no session is active for this source right now.
/// </param>
public sealed record ClosedHourAudioStillRecording(double RecordedSeconds) : ClosedHourAudioResult;

public sealed record ClosedHourAudioNotAvailable(string Reason) : ClosedHourAudioResult;
