namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// HTTP response body for a non-200 GET /audio/segment response (a 200 returns the audio bytes
/// directly, no JSON envelope).
/// </summary>
public sealed class AudioSegmentErrorResponse
{
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Set only for a 409 (still recording) — how far the live session has actually captured into
    /// the current hour, per ILiveCaptureProgressReader.
    /// </summary>
    public double? RecordedSeconds { get; set; }
}
