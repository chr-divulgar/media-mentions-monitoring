using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Body of POST /transcribe: the WAV chunks the portal cut (16 kHz, mono) and the language to recognize.</summary>
public sealed class TranscribeRequest
{
    // Two hours of one-minute chunks: far more than a note's clip, and a bound on what one request can ask for.
    public const int MaxFiles = 120;

    public const string DefaultLanguage = "es-CO";

    public List<string>? AudioPaths { get; set; }

    public string? Language { get; set; }

    public string EffectiveLanguage => string.IsNullOrWhiteSpace(Language) ? DefaultLanguage : Language.Trim();

    /// <summary>The reason the request cannot be served, or null when it can.</summary>
    public string? Validate()
    {
        if (AudioPaths is null || AudioPaths.Count == 0)
        {
            return "audioPaths is required: the WAV files to transcribe.";
        }

        if (AudioPaths.Count > MaxFiles)
        {
            return $"At most {MaxFiles} files can be transcribed in one request.";
        }

        return AudioPaths.Any(string.IsNullOrWhiteSpace) ? "audioPaths has an empty path." : null;
    }
}

/// <summary>Answer of POST /transcribe: one result per path, in the order asked.</summary>
public sealed class TranscribeResponse
{
    public required IReadOnlyList<TranscriptionChunkResult> Chunks { get; init; }
}
