namespace MediaOpsCore.Modules.Capture.Application;

public sealed class PluginProfile
{
    public PluginProfile(
        string pluginId,
        string media,
        string? platform,
        IngestionMode ingestionMode,
        TimeSpan flacWindowDuration,
        TimeSpan opusFlushInterval,
        TimeSpan opusRotationInterval)
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(pluginId))
            : pluginId;
        Media = string.IsNullOrWhiteSpace(media)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(media))
            : media;
        Platform = string.IsNullOrWhiteSpace(platform) ? null : platform;
        IngestionMode = ingestionMode;
        FlacWindowDuration = flacWindowDuration <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(flacWindowDuration), "FLAC window duration must be greater than zero.")
            : flacWindowDuration;
        OpusFlushInterval = opusFlushInterval <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(opusFlushInterval), "OPUS flush interval must be greater than zero.")
            : opusFlushInterval;
        OpusRotationInterval = opusRotationInterval <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(opusRotationInterval), "OPUS rotation interval must be greater than zero.")
            : opusRotationInterval;
    }

    public string PluginId { get; }

    public string Media { get; }

    public string? Platform { get; }

    public IngestionMode IngestionMode { get; }

    public TimeSpan FlacWindowDuration { get; }

    public TimeSpan OpusFlushInterval { get; }

    public TimeSpan OpusRotationInterval { get; }
}

