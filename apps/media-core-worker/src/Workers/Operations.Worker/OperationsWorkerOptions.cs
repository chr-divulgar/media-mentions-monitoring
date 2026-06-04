namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorkerOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan DiscreteWorkerInterval { get; set; } = TimeSpan.FromMinutes(5);

    public string CaptureToolExecutable { get; set; } = "cmd.exe";

    public string CaptureToolArgumentsTemplate { get; set; } = "/c echo capture {url}";

    public TimeSpan CaptureCommandTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public string PluginProfilesFilePath { get; set; } = "stage/plugin-profiles.json";

    public string ContinuousMediaAllowList { get; set; } = "radio,video";

    public int SegmentDurationSeconds { get; set; } = 30;

    public TimeSpan ProcessGuardianTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ProcessGuardianRestartCommandTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public string StageFilesystemRootPath { get; set; } = "stage-evidence";

    public bool EnableCanaryMode { get; set; } = true;

    public int CanaryPlatformPercent { get; set; } = 20;

    public int CanaryPlatformMinPercent { get; set; } = 10;

    public int CanaryPlatformMaxPercent { get; set; } = 100;

    public string? CanaryPlatformAllowList { get; set; }

    public string CaptureSourcesFilePath { get; set; } = "stage/capture-sources.json";
}