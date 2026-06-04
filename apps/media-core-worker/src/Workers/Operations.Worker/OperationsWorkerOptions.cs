namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorkerOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public string TenantId { get; set; } = "default";

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
}