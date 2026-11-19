using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The speech engines the worker can run, by the name worker-options.json gives in "speechEngine". A new engine
/// (a licensed cloud service, a local model) is an <see cref="ISpeechToText"/> class plus one line here.
/// </summary>
public static class SpeechEngines
{
    public const string GoogleWeb = "google-web";

    public static ISpeechToText Create(string? engine) =>
        (engine ?? GoogleWeb).Trim().ToLowerInvariant() switch
        {
            GoogleWeb => new GoogleWebSpeechToText(GoogleWebSpeechToText.ResolveApiKey()),
            _ => throw new InvalidOperationException(
                $"Unknown speechEngine '{engine}'. Available engines: {GoogleWeb}."),
        };
}
