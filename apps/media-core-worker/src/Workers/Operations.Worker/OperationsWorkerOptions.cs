namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorkerOptions
{
    public TimeSpan DiscreteWorkerInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SegmentationInterval { get; set; } = TimeSpan.FromSeconds(30);

    public string PluginProfilesFilePath { get; set; } = "stage/plugin-profiles.json";

    public string ContinuousMediaAllowList { get; set; } = "radio,video";

    public int CaptureMaxDegreeOfParallelism { get; set; } = 64;

    public int SegmentDurationSeconds { get; set; } = 30;

    public string StageFilesystemRootPath { get; set; } = "stage-evidence";

    public string AudioOutputRootPath { get; set; } = ".";

    public int DefaultFlacWindowDurationSeconds { get; set; } = 30;

    public int DefaultOpusFlushIntervalSeconds { get; set; } = 30;

    public int DefaultOpusRotationIntervalHours { get; set; } = 1;

    public int DefaultOpusBitrateKbps { get; set; } = 64;

    public bool EnableDecoderReconnect { get; set; } = true;

    public int DecoderReconnectDelayMaxSeconds { get; set; } = 5;

    public bool RtspPreferTcp { get; set; } = true;

    public bool EnableFlacSilenceChunking { get; set; } = true;

    public int FlacSilenceMinChunkSeconds { get; set; } = 20;

    public int FlacSilenceMaxChunkSeconds { get; set; } = 30;

    public int FlacSilenceHoldMilliseconds { get; set; } = 300;

    public int FlacSilenceAnalysisWindowMilliseconds { get; set; } = 20;

    public double FlacSilenceAdaptiveThresholdMultiplier { get; set; } = 1.7;

    public double FlacSilenceNoiseFloorEmaAlpha { get; set; } = 0.08;

    public double FlacSilenceHighPassCutoffHz { get; set; } = 120;

    public bool EnableCanaryMode { get; set; } = true;

    public int CanaryPlatformPercent { get; set; } = 20;

    public int CanaryPlatformMinPercent { get; set; } = 10;

    public int CanaryPlatformMaxPercent { get; set; } = 100;

    public string? CanaryPlatformAllowList { get; set; }

    public string CaptureSourcesFilePath { get; set; } = "stage/capture-sources.json";

    public bool EnableStartupValidation { get; set; } = true;

    public bool EnableStartupDiscoveryOnFailedOnly { get; set; } = true;

    public int StartupValidationTimeoutSeconds { get; set; } = 12;

    public int StartupDiscoveryRequestTimeoutSeconds { get; set; } = 10;
}

