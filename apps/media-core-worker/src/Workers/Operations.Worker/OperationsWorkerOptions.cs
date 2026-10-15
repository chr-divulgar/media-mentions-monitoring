using MediaOpsCore.Modules.Capture.Application;

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

    // Measured against this machine's actual D:\ usage (~30 GB/day for ~67 radio sources, see
    // PLAN_MAESTRO.md F1): the local disk is a rolling buffer, not the archive — someone copies
    // what they need off it (FileZilla) before it ages out. Keep this short; raise it only once
    // AudioOutputRootPath points at a drive/volume sized for more.
    public int AudioRetentionDays { get; set; } = 3;

    // Below this, RetentionPurgeWorker logs a "disk-low" incident (visible on every source's
    // /status row) in addition to its normal age-based purge — a loud signal that retention alone
    // isn't keeping up, before the drive actually fills and capture starts failing.
    public int MinFreeDiskGb { get; set; } = 10;

    // TV as hourly MP4 (TvVideoRecorderWorker), alongside the audio. Off by default: roughly
    // 0.25–0.45 GB per channel-hour at 480p, on top of the audio's disk use.
    public bool TvVideoEnabled { get; set; } = false;

    // 240 is YouTube's 426x240 rendition: copied as it is served (no CPU) at about a third of the size of 480p.
    public int TvVideoMaxHeight { get; set; } = 240;

    // The audio of a TV recording is re-encoded to mono AAC at this bitrate and sample rate (YouTube's own is 128 kbps
    // stereo, a third of the file); 0 keeps the audio as it comes. 32 kbps at 16 kHz was transcribed as well as the
    // original (99 % word match on real channels).
    public int TvAudioKbps { get; set; } = 32;

    public int TvAudioSampleRateHz { get; set; } = 16000;


    // Archive of closed capture files (ArchiveUploadWorker). Only active when ArchiveEnabled AND the
    // ARCHIVE_FTP_* credentials are in the environment; then the purge deletes archived files only.
    public bool ArchiveEnabled { get; set; } = false;

    public bool ArchiveFtpRequireTls { get; set; } = true;

    public bool ArchiveFtpAllowInvalidCertificate { get; set; } = false;

    // Name the server's certificate is issued for, when it differs from the host in ARCHIVE_FTP_URL
    // (e.g. connecting by IP): a name mismatch is then accepted if the certificate is valid for this
    // name. Safer than ArchiveFtpAllowInvalidCertificate, which accepts any certificate at all.
    public string? ArchiveFtpCertificateHost { get; set; }

    public int ArchiveMaxParallelUploads { get; set; } = 2;

    public int ArchiveMinIdleMinutes { get; set; } = 20;

    // Radio is archived as one small mono opus per hour. 24 kbps at 16 kHz was transcribed as well as the 32 kbps mp3 it
    // replaced and weighs 24 % less (10.6 MB an hour; 16 kbps is half the size but loses about 3.5 points of word match).
    public int ArchiveRadioKbps { get; set; } = 24;

    public int ArchiveRadioSampleRateHz { get; set; } = 16000;

    // How long the archive keeps a day of radio and of TV before ArchiveRetentionWorker deletes it from the
    // server; each medium has its own account and space, so each has its own days. This is the company's backup of
    // the raw recordings (the notes and their supports are what the contract requires keeping: AMB-026).
    // 0 never deletes. Anything between 1 and 6 is treated as 7 (see ArchiveRemoteRetention.EffectiveDays), so a
    // typo cannot wipe last week's recordings.
    public int ArchiveRetentionDaysRadio { get; set; } = 10;

    public int ArchiveRetentionDaysTelevision { get; set; } = 10;

    public int RetentionDaysFor(string medium) => medium switch
    {
        ArchiveMedia.Radio => ArchiveRetentionDaysRadio,
        ArchiveMedia.Television => ArchiveRetentionDaysTelevision,
        _ => 0,
    };

    // Recordings of earlier days are fetched back from the archive for the alert editor into
    // <StageFilesystemRootPath>/video-cache; one is kept this long and the folder is capped in size.
    public int VideoCacheHours { get; set; } = 24;

    public int VideoCacheMaxGb { get; set; } = 10;

    // The same for radio: an hour archived as mp3 (~14 MB) is fetched into <StageFilesystemRootPath>/audio-cache
    // when an alert of an earlier day is edited.
    public int AudioCacheHours { get; set; } = 24;

    public int AudioCacheMaxGb { get; set; } = 2;

    // Headless Chromium (HeadlessBrowser) for outlets behind an anti-bot challenge, and later the
    // PDF copy of each note. Downloaded on first use into this folder (relative to the app root).
    public bool HeadlessBrowserEnabled { get; set; } = true;

    public string HeadlessBrowserPath { get; set; } = "bin/ms-playwright";

    // From ARCHIVE_FTP_URL / _USER / _PASSWORD only — never from worker-options.json.
    // The account every medium used before they had their own; still the fallback for a medium without one.
    public FtpArchiveCredentials? ArchiveFtp { get; set; }

    // The account of each medium (ArchiveMedia.Radio, Television, Press), from ARCHIVE_<MEDIUM>_FTP_*.
    public Dictionary<string, FtpArchiveCredentials> ArchiveFtpByMedium { get; } = new(StringComparer.Ordinal);

    public FtpArchiveCredentials? CredentialsFor(string medium) =>
        ArchiveFtpByMedium.TryGetValue(medium, out var own) ? own : ArchiveFtp;

    // From ARCHIVE_PUBLIC_BASE_URL only: the https address under which the archive's FTP folder is
    // published, so a note's PDF gets a link anyone with access can open. Null disables note PDFs.
    public string? ArchivePublicBaseUrl { get; set; }

    // The https address of each medium's account, from ARCHIVE_<MEDIUM>_PUBLIC_BASE_URL: what turns a path in the archive
    // into a link anyone can open.
    public Dictionary<string, string> ArchivePublicBaseUrlByMedium { get; } = new(StringComparer.Ordinal);

    // The medium's own address; for the press, the single one used before when it has none of its own.
    public string? PublicBaseUrlFor(string medium) =>
        ArchivePublicBaseUrlByMedium.TryGetValue(medium, out var own)
            ? own
            : medium == ArchiveMedia.Press ? ArchivePublicBaseUrl : null;

    public bool IsArchiveActive => ArchiveEnabled && (ArchiveFtp is not null || ArchiveFtpByMedium.Count > 0);

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


    public bool EnableStartupValidation { get; set; } = true;

    public bool EnableStartupDiscoveryOnFailedOnly { get; set; } = true;

    public int StartupValidationTimeoutSeconds { get; set; } = 12;

    // The worker verifies internet access before anything else and waits (retrying every
    // ConnectivityCheckIntervalSeconds) while there is none; see ConnectivityMonitor.
    public bool ConnectivityCheckEnabled { get; set; } = true;

    // Comma-separated URLs; reaching any one of them counts as online. Empty uses the defaults.
    public string ConnectivityCheckUrls { get; set; } = string.Empty;

    public int ConnectivityCheckIntervalSeconds { get; set; } = 60;

    public int ConnectivityCheckTimeoutSeconds { get; set; } = 8;

    public int StartupDiscoveryRequestTimeoutSeconds { get; set; } = 10;

    public string YtdlpBinDirectory { get; set; } = "bin";

    public int YtdlpResolutionTimeoutSeconds { get; set; } = 60;

    // Plain check of a channel's /live page before yt-dlp; off makes every retry go to yt-dlp.
    public bool YoutubeLiveProbeEnabled { get; set; } = true;

    public bool UseBrowserCookies { get; set; } = false;

    public string BrowserCookiesSource { get; set; } = "edge";

    public string? YoutubeCookiesFilePath { get; set; }

    public string YoutubeCookiesAlertFilePath { get; set; } = "stage/cookies/youtube-auth-required.flag";

    // Alert email channel. Null (the default) means email recipients are skipped; set only when
    // SMTP_HOST and SMTP_FROM are present in the environment (credentials never live in the JSON).
    public MediaOpsCore.Modules.Alerting.Infrastructure.SmtpAlertOptions? Smtp { get; set; }

    // Same connection string as apps/web-api's MONGODB_URI so both point at the one MongoDB instance.
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";

    public string MongoConfigDatabaseName { get; set; } = "config";

    public string MongoMonitoringDatabaseName { get; set; } = "monitoring";

    // "workerAlert" during the shadow-run validation period; becomes "alert" at cutover (config-only change).
    public string MongoAlertCollectionName { get; set; } = "workerAlert";

    // Loopback port the Node.js/Baileys sidecar listens on (see WhatsAppSidecarProcessSupervisor).
    public int WhatsAppSidecarPort { get; set; } = 5101;

    public int WhatsAppSidecarStartupTimeoutSeconds { get; set; } = 30;

    // Outside the publish/output tree so Baileys' auth session survives redeploys, same reasoning
    // as StageFilesystemRootPath.
    public string WhatsAppAuthStatePath { get; set; } = "stage/whatsapp-auth";

    // Off by default: the Google News RSS endpoint behind this is unofficial, and enabling it
    // starts making real outbound requests every DiscreteWorkerInterval. Turn on explicitly once
    // verified (see PLAN_MAESTRO.md's web-news plan for the manual verification steps).
    public bool WebNewsEnabled { get; set; } = false;

    // Daily headlines digest (RQ-035): off by default because it messages every client's internet
    // recipients; DailyHeadlinesTime is a Bogota wall-clock "HH:mm".
    public bool DailyHeadlinesEnabled { get; set; } = false;

    public string DailyHeadlinesTime { get; set; } = "07:00";

    public int WebNewsKeywordsPerQuery { get; set; } = 8;

    public int WebNewsMaxConcurrency { get; set; } = 4;

    public int WebNewsRequestTimeoutSeconds { get; set; } = 20;

    // A failed article (timeout, HTTP error, unreadable page) is retried on later cycles instead of
    // being given up on for the rest of the 24h seen-window, since some failures are transient — but
    // only up to this many attempts, so a site that blocks every request (WAF/bot protection) isn't
    // hammered for a full day. 3 attempts 30 minutes apart gives a transient failure about an hour
    // to clear on its own.
    public int WebNewsMaxFailedAttempts { get; set; } = 3;
}

