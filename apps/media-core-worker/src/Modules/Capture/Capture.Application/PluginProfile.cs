namespace MediaOpsCore.Modules.Capture.Application;

public sealed class PluginProfile
{
    public PluginProfile(
        string pluginId,
        string media,
        string? platform,
        IngestionMode ingestionMode,
        string toolExecutable,
        string toolArgumentsTemplate,
        TimeSpan commandTimeout)
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(pluginId))
            : pluginId;
        Media = string.IsNullOrWhiteSpace(media)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(media))
            : media;
        Platform = string.IsNullOrWhiteSpace(platform) ? null : platform;
        IngestionMode = ingestionMode;
        ToolExecutable = string.IsNullOrWhiteSpace(toolExecutable)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(toolExecutable))
            : toolExecutable;
        ToolArgumentsTemplate = string.IsNullOrWhiteSpace(toolArgumentsTemplate)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(toolArgumentsTemplate))
            : toolArgumentsTemplate;
        CommandTimeout = commandTimeout <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(commandTimeout), "Command timeout must be greater than zero.")
            : commandTimeout;
    }

    public string PluginId { get; }

    public string Media { get; }

    public string? Platform { get; }

    public IngestionMode IngestionMode { get; }

    public string ToolExecutable { get; }

    public string ToolArgumentsTemplate { get; }

    public TimeSpan CommandTimeout { get; }
}