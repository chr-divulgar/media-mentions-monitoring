namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// One transcribed chunk of a source's audio: what the speech engine returned for [StartTime, EndTime] of the hour
/// file FilePath. Every chunk is saved whatever came back (Status: recognized, no_speech, rate_limited, ...), so a gap
/// in the hour always means the audio was never sent, not that recognition ran and found nothing.
/// </summary>
public sealed record TranscriptionEntry(
    string SourceId,
    string Platform,
    string Media,
    string FilePath,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Text,
    string Status);

/// <summary>
/// Outbound port: where the transcriptions go (the `transcription` collection of the monitoring database). It replaced
/// the JSON file the pipeline kept next to each hour of audio, which was rewritten whole for every chunk and deleted
/// with the audio.
/// </summary>
public interface ITranscriptionStore
{
    /// <summary>The entries of one hour file, oldest first: the context the next chunk is de-duplicated against.</summary>
    Task<IReadOnlyList<TranscriptionEntry>> LoadHourAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Saves one entry. An entry for the same hour file and times replaces the earlier one: saving twice is harmless.</summary>
    Task AppendAsync(TranscriptionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The same for many at once (the import of the JSON files that were already on disk).</summary>
    Task AppendManyAsync(IReadOnlyList<TranscriptionEntry> entries, CancellationToken cancellationToken = default);
}
