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

    public string YtdlpBinDirectory { get; set; } = "bin";

    public int YtdlpResolutionTimeoutSeconds { get; set; } = 60;

    public bool UseBrowserCookies { get; set; } = false;

    public string BrowserCookiesSource { get; set; } = "edge";

    public string? YoutubeCookiesFilePath { get; set; }

    public string YoutubeCookiesAlertFilePath { get; set; } = "stage/cookies/youtube-auth-required.flag";

    public FirestoreCaptureSourceRepositoryOptions? Firestore { get; set; }

    // Same connection string as apps/web-api's MONGODB_URI so both point at the one MongoDB instance.
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";

    public string MongoConfigDatabaseName { get; set; } = "config";

    public string MongoMonitoringDatabaseName { get; set; } = "monitoring";

    // "workerAlert" during the shadow-run validation period; becomes "alert" at cutover (config-only change).
    public string MongoAlertCollectionName { get; set; } = "workerAlert";
}

