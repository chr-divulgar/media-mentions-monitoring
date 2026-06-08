namespace MediaOpsCore.Modules.Capture.Application;

public sealed class PluginExecutionPlan
{
    public PluginExecutionPlan(
        string pluginId,
        TimeSpan flacWindowDuration,
        TimeSpan opusFlushInterval,
        TimeSpan opusRotationInterval)
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(pluginId))
            : pluginId;
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

    public TimeSpan FlacWindowDuration { get; }

    public TimeSpan OpusFlushInterval { get; }

    public TimeSpan OpusRotationInterval { get; }
}

