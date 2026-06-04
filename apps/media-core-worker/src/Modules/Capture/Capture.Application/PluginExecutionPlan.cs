namespace MediaOpsCore.Modules.Capture.Application;

public sealed class PluginExecutionPlan
{
    public PluginExecutionPlan(
        string pluginId,
        string toolExecutable,
        string toolArgumentsTemplate,
        TimeSpan commandTimeout)
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId)
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(pluginId))
            : pluginId;
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

    public string ToolExecutable { get; }

    public string ToolArgumentsTemplate { get; }

    public TimeSpan CommandTimeout { get; }
}