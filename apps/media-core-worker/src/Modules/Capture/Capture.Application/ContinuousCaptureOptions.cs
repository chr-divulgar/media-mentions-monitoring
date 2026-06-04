namespace MediaOpsCore.Modules.Capture.Application;

public sealed class ContinuousCaptureOptions
{
    public string ToolExecutable { get; set; } = "cmd.exe";

    public string ToolArgumentsTemplate { get; set; } = "/c echo capture {url}";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(15);
}