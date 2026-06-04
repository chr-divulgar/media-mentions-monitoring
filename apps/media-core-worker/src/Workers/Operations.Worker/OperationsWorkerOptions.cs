namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorkerOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public string CaptureSourceId { get; set; } = "source-default";

    public string CapturePlatform { get; set; } = "radio";

    public string CaptureMedia { get; set; } = "generic";

    public string CaptureStreamUrl { get; set; } = "https://example.com/live";

    public string CaptureToolExecutable { get; set; } = "cmd.exe";

    public string CaptureToolArgumentsTemplate { get; set; } = "/c echo capture {url}";

    public TimeSpan CaptureCommandTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int SegmentDurationSeconds { get; set; } = 30;

    public TimeSpan ProcessGuardianTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ProcessGuardianRestartCommandTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public bool EnableStageDatabaseMirror { get; set; }

    public string StageDatabaseBaseUrl { get; set; } = string.Empty;

    public string StageDatabaseRootPath { get; set; } = "monitoringArtifacts";

    public string? StageDatabaseAuthToken { get; set; }

    public string StageFilesystemRootPath { get; set; } = "stage-evidence";

    public bool EnableShadowMode { get; set; } = true;

    public string LegacySnapshotFilePath { get; set; } = "stage/legacy-snapshot.json";

    public double ShadowParityMinimumPercent { get; set; } = 95;

    public bool EnableCanaryMode { get; set; } = true;

    public int CanaryPlatformPercent { get; set; } = 20;

    public int CanaryPlatformMinPercent { get; set; } = 10;

    public int CanaryPlatformMaxPercent { get; set; } = 100;

    public int CanaryIncreaseStepPercent { get; set; } = 5;

    public int CanaryDecreaseStepPercent { get; set; } = 5;

    public string CanaryPromotionMilestones { get; set; } = "20,50,100";

    public int CanaryStableCyclesForPromotion { get; set; } = 3;

    public string? CanaryPlatformAllowList { get; set; }

    public string CaptureSourcesFilePath { get; set; } = "stage/capture-sources.json";
}