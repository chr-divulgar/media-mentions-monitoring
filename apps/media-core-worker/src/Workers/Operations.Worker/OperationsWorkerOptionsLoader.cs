using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

public static class OperationsWorkerOptionsLoader
{
    private const string DefaultConfigPath = "stage/worker-options.json";
    private const string StageDirectoryName = "stage";

    // <prefix>_URL / _USER / _PASSWORD from the environment, or null unless all three are there and the URL is valid.
    private static FtpArchiveCredentials? ReadArchiveCredentials(string prefix)
    {
        var url = Environment.GetEnvironmentVariable(prefix + "_URL");
        var user = Environment.GetEnvironmentVariable(prefix + "_USER");
        var password = Environment.GetEnvironmentVariable(prefix + "_PASSWORD");
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out _)
            && !string.IsNullOrWhiteSpace(user) && password is not null
            ? new FtpArchiveCredentials(url.Trim(), user.Trim(), password)
            : null;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record WorkerOptionsFileModel(
        int? SegmentationIntervalSeconds,
        int? DiscreteWorkerIntervalSeconds,
        string? PluginProfilesFilePath,
        string? ContinuousMediaAllowList,
        int? CaptureMaxDegreeOfParallelism,
        bool? EnableCanaryMode,
        int? CanaryPlatformPercent,
        int? CanaryPlatformMinPercent,
        int? CanaryPlatformMaxPercent,
        string? CanaryPlatformAllowList,
        string? StageFilesystemRootPath,
        int? SegmentDurationSeconds,
        string? AudioOutputRootPath,
        int? AudioRetentionDays,
        int? AudioRetentionDaysTelevision,
        int? MinFreeDiskGb,
        bool? TvVideoEnabled,
        int? TvVideoMaxHeight,
        int? TvAudioKbps,
        int? TvAudioSampleRateHz,
        bool? ArchiveEnabled,
        bool? ArchiveFtpRequireTls,
        bool? ArchiveFtpAllowInvalidCertificate,
        string? ArchiveFtpCertificateHost,
        int? ArchiveMaxParallelUploads,
        int? ArchiveMinIdleMinutes,
        int? ArchiveRadioKbps,
        int? ArchiveRetentionDaysRadio,
        int? ArchiveRetentionDaysTelevision,
        int? ArchiveRadioSampleRateHz,
        int? VideoCacheHours,
        int? VideoCacheMaxGb,
        int? AudioCacheHours,
        int? AudioCacheMaxGb,
        bool? HeadlessBrowserEnabled,
        string? HeadlessBrowserPath,
        int? DefaultFlacWindowDurationSeconds,
        int? DefaultOpusFlushIntervalSeconds,
        int? DefaultOpusRotationIntervalHours,
        int? DefaultOpusBitrateKbps,
        bool? EnableDecoderReconnect,
        int? DecoderReconnectDelayMaxSeconds,
        bool? RtspPreferTcp,
        bool? EnableFlacSilenceChunking,
        int? FlacSilenceMinChunkSeconds,
        int? FlacSilenceMaxChunkSeconds,
        int? FlacSilenceHoldMilliseconds,
        int? FlacSilenceAnalysisWindowMilliseconds,
        double? FlacSilenceAdaptiveThresholdMultiplier,
        double? FlacSilenceNoiseFloorEmaAlpha,
        double? FlacSilenceHighPassCutoffHz,
        bool? EnableStartupValidation,
        bool? EnableStartupDiscoveryOnFailedOnly,
        int? StartupValidationTimeoutSeconds,
        int? StartupDiscoveryRequestTimeoutSeconds,
        string? YtdlpBinDirectory,
        int? YtdlpResolutionTimeoutSeconds,
        bool? YoutubeLiveProbeEnabled,
        bool? UseBrowserCookies,
        string? BrowserCookiesSource,
        string? YoutubeCookiesFilePath,
        string? YoutubeCookiesAlertFilePath,
        string? MongoConnectionString,
        string? MongoConfigDatabaseName,
        string? MongoMonitoringDatabaseName,
        string? MongoAlertCollectionName,
        int? WhatsAppSidecarPort,
        int? WhatsAppSidecarStartupTimeoutSeconds,
        string? WhatsAppAuthStatePath,
        bool? WebNewsEnabled,
        int? WebNewsKeywordsPerQuery,
        int? WebNewsMaxConcurrency,
        int? WebNewsRequestTimeoutSeconds,
        int? WebNewsMaxFailedAttempts,
        bool? DailyHeadlinesEnabled,
        string? DailyHeadlinesTime,
        bool? ConnectivityCheckEnabled = null,
        string? ConnectivityCheckUrls = null,
        int? ConnectivityCheckIntervalSeconds = null,
        int? ConnectivityCheckTimeoutSeconds = null);

    public static OperationsWorkerOptions Load(string? configPath = null)
    {
        var options = new OperationsWorkerOptions();
        var path = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath;
        var resolvedConfigPath = ResolveConfigPath(path);
        var configDirectory = Path.GetDirectoryName(resolvedConfigPath);
        var applicationRoot = ResolveApplicationRoot(configDirectory);

        if (!File.Exists(resolvedConfigPath))
        {
            return NormalizeConfiguredPaths(options, applicationRoot, configDirectory);
        }

        var json = File.ReadAllText(resolvedConfigPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return NormalizeConfiguredPaths(options, applicationRoot, configDirectory);
        }

        var model = JsonSerializer.Deserialize<WorkerOptionsFileModel>(json, SerializerOptions);
        if (model is null)
        {
            return NormalizeConfiguredPaths(options, applicationRoot, configDirectory);
        }

        if (model.SegmentationIntervalSeconds.HasValue)
        {
            options.SegmentationInterval = TimeSpan.FromSeconds(model.SegmentationIntervalSeconds.Value);
        }

        if (model.DiscreteWorkerIntervalSeconds.HasValue)
        {
            options.DiscreteWorkerInterval = TimeSpan.FromSeconds(model.DiscreteWorkerIntervalSeconds.Value);
        }

        if (!string.IsNullOrWhiteSpace(model.PluginProfilesFilePath))
        {
            options.PluginProfilesFilePath = model.PluginProfilesFilePath;
        }

        if (!string.IsNullOrWhiteSpace(model.ContinuousMediaAllowList))
        {
            options.ContinuousMediaAllowList = model.ContinuousMediaAllowList;
        }

        if (model.CaptureMaxDegreeOfParallelism.HasValue)
        {
            options.CaptureMaxDegreeOfParallelism = model.CaptureMaxDegreeOfParallelism.Value;
        }

        if (model.EnableCanaryMode.HasValue)
        {
            options.EnableCanaryMode = model.EnableCanaryMode.Value;
        }

        if (model.CanaryPlatformPercent.HasValue)
        {
            options.CanaryPlatformPercent = model.CanaryPlatformPercent.Value;
        }

        if (model.CanaryPlatformMinPercent.HasValue)
        {
            options.CanaryPlatformMinPercent = model.CanaryPlatformMinPercent.Value;
        }

        if (model.CanaryPlatformMaxPercent.HasValue)
        {
            options.CanaryPlatformMaxPercent = model.CanaryPlatformMaxPercent.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.CanaryPlatformAllowList))
        {
            options.CanaryPlatformAllowList = model.CanaryPlatformAllowList;
        }

        if (!string.IsNullOrWhiteSpace(model.StageFilesystemRootPath))
        {
            options.StageFilesystemRootPath = model.StageFilesystemRootPath;
        }

        if (model.SegmentDurationSeconds.HasValue)
        {
            options.SegmentDurationSeconds = model.SegmentDurationSeconds.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.AudioOutputRootPath))
        {
            options.AudioOutputRootPath = model.AudioOutputRootPath;
        }

        if (model.AudioRetentionDays.HasValue)
        {
            // Floored at 0 (keep only today): a negative value would put the purge cutoff after
            // today and delete today's own folder.
            options.AudioRetentionDays = Math.Max(0, model.AudioRetentionDays.Value);
        }

        if (model.AudioRetentionDaysTelevision.HasValue)
        {
            options.AudioRetentionDaysTelevision = Math.Max(0, model.AudioRetentionDaysTelevision.Value);
        }

        if (model.MinFreeDiskGb.HasValue)
        {
            options.MinFreeDiskGb = model.MinFreeDiskGb.Value;
        }

        if (model.TvVideoEnabled.HasValue)
        {
            options.TvVideoEnabled = model.TvVideoEnabled.Value;
        }

        if (model.TvVideoMaxHeight is > 0)
        {
            options.TvVideoMaxHeight = model.TvVideoMaxHeight.Value;
        }

        if (model.TvAudioKbps is >= 0)
        {
            options.TvAudioKbps = model.TvAudioKbps.Value;
        }

        if (model.TvAudioSampleRateHz is > 0)
        {
            options.TvAudioSampleRateHz = model.TvAudioSampleRateHz.Value;
        }

        if (model.ArchiveEnabled.HasValue)
        {
            options.ArchiveEnabled = model.ArchiveEnabled.Value;
        }

        if (model.ArchiveFtpRequireTls.HasValue)
        {
            options.ArchiveFtpRequireTls = model.ArchiveFtpRequireTls.Value;
        }

        if (model.ArchiveFtpAllowInvalidCertificate.HasValue)
        {
            options.ArchiveFtpAllowInvalidCertificate = model.ArchiveFtpAllowInvalidCertificate.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.ArchiveFtpCertificateHost))
        {
            options.ArchiveFtpCertificateHost = model.ArchiveFtpCertificateHost.Trim();
        }

        if (model.ArchiveMaxParallelUploads is > 0)
        {
            options.ArchiveMaxParallelUploads = model.ArchiveMaxParallelUploads.Value;
        }

        if (model.VideoCacheHours is > 0)
        {
            options.VideoCacheHours = model.VideoCacheHours.Value;
        }

        if (model.VideoCacheMaxGb is > 0)
        {
            options.VideoCacheMaxGb = model.VideoCacheMaxGb.Value;
        }

        if (model.AudioCacheHours is > 0)
        {
            options.AudioCacheHours = model.AudioCacheHours.Value;
        }

        if (model.AudioCacheMaxGb is > 0)
        {
            options.AudioCacheMaxGb = model.AudioCacheMaxGb.Value;
        }

        if (model.ArchiveRetentionDaysRadio is >= 0)
        {
            options.ArchiveRetentionDaysRadio = model.ArchiveRetentionDaysRadio.Value;
        }

        if (model.ArchiveRetentionDaysTelevision is >= 0)
        {
            options.ArchiveRetentionDaysTelevision = model.ArchiveRetentionDaysTelevision.Value;
        }

        if (model.ArchiveRadioKbps is > 0)
        {
            options.ArchiveRadioKbps = model.ArchiveRadioKbps.Value;
        }

        if (model.ArchiveRadioSampleRateHz is > 0)
        {
            options.ArchiveRadioSampleRateHz = model.ArchiveRadioSampleRateHz.Value;
        }

        if (model.ArchiveMinIdleMinutes is > 0)
        {
            options.ArchiveMinIdleMinutes = model.ArchiveMinIdleMinutes.Value;
        }

        if (model.HeadlessBrowserEnabled.HasValue)
        {
            options.HeadlessBrowserEnabled = model.HeadlessBrowserEnabled.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.HeadlessBrowserPath))
        {
            options.HeadlessBrowserPath = model.HeadlessBrowserPath;
        }

        if (model.DefaultFlacWindowDurationSeconds.HasValue)
        {
            options.DefaultFlacWindowDurationSeconds = model.DefaultFlacWindowDurationSeconds.Value;
        }

        if (model.DefaultOpusFlushIntervalSeconds.HasValue)
        {
            options.DefaultOpusFlushIntervalSeconds = model.DefaultOpusFlushIntervalSeconds.Value;
        }

        if (model.DefaultOpusRotationIntervalHours.HasValue)
        {
            options.DefaultOpusRotationIntervalHours = model.DefaultOpusRotationIntervalHours.Value;
        }

        if (model.DefaultOpusBitrateKbps.HasValue)
        {
            options.DefaultOpusBitrateKbps = model.DefaultOpusBitrateKbps.Value;
        }

        if (model.EnableDecoderReconnect.HasValue)
        {
            options.EnableDecoderReconnect = model.EnableDecoderReconnect.Value;
        }

        if (model.DecoderReconnectDelayMaxSeconds.HasValue)
        {
            options.DecoderReconnectDelayMaxSeconds = model.DecoderReconnectDelayMaxSeconds.Value;
        }

        if (model.RtspPreferTcp.HasValue)
        {
            options.RtspPreferTcp = model.RtspPreferTcp.Value;
        }

        if (model.EnableFlacSilenceChunking.HasValue)
        {
            options.EnableFlacSilenceChunking = model.EnableFlacSilenceChunking.Value;
        }

        if (model.FlacSilenceMinChunkSeconds.HasValue)
        {
            options.FlacSilenceMinChunkSeconds = model.FlacSilenceMinChunkSeconds.Value;
        }

        if (model.FlacSilenceMaxChunkSeconds.HasValue)
        {
            options.FlacSilenceMaxChunkSeconds = model.FlacSilenceMaxChunkSeconds.Value;
        }

        if (model.FlacSilenceHoldMilliseconds.HasValue)
        {
            options.FlacSilenceHoldMilliseconds = model.FlacSilenceHoldMilliseconds.Value;
        }

        if (model.FlacSilenceAnalysisWindowMilliseconds.HasValue)
        {
            options.FlacSilenceAnalysisWindowMilliseconds = model.FlacSilenceAnalysisWindowMilliseconds.Value;
        }

        if (model.FlacSilenceAdaptiveThresholdMultiplier.HasValue)
        {
            options.FlacSilenceAdaptiveThresholdMultiplier = model.FlacSilenceAdaptiveThresholdMultiplier.Value;
        }

        if (model.FlacSilenceNoiseFloorEmaAlpha.HasValue)
        {
            options.FlacSilenceNoiseFloorEmaAlpha = model.FlacSilenceNoiseFloorEmaAlpha.Value;
        }

        if (model.FlacSilenceHighPassCutoffHz.HasValue)
        {
            options.FlacSilenceHighPassCutoffHz = model.FlacSilenceHighPassCutoffHz.Value;
        }

        if (model.EnableStartupValidation.HasValue)
        {
            options.EnableStartupValidation = model.EnableStartupValidation.Value;
        }

        if (model.EnableStartupDiscoveryOnFailedOnly.HasValue)
        {
            options.EnableStartupDiscoveryOnFailedOnly = model.EnableStartupDiscoveryOnFailedOnly.Value;
        }

        if (model.ConnectivityCheckEnabled.HasValue)
        {
            options.ConnectivityCheckEnabled = model.ConnectivityCheckEnabled.Value;
        }

        if (model.ConnectivityCheckUrls is not null)
        {
            options.ConnectivityCheckUrls = model.ConnectivityCheckUrls;
        }

        if (model.ConnectivityCheckIntervalSeconds is > 0)
        {
            options.ConnectivityCheckIntervalSeconds = model.ConnectivityCheckIntervalSeconds.Value;
        }

        if (model.ConnectivityCheckTimeoutSeconds is > 0)
        {
            options.ConnectivityCheckTimeoutSeconds = model.ConnectivityCheckTimeoutSeconds.Value;
        }

        if (model.StartupValidationTimeoutSeconds.HasValue)
        {
            options.StartupValidationTimeoutSeconds = model.StartupValidationTimeoutSeconds.Value;
        }

        if (model.StartupDiscoveryRequestTimeoutSeconds.HasValue)
        {
            options.StartupDiscoveryRequestTimeoutSeconds = model.StartupDiscoveryRequestTimeoutSeconds.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.YtdlpBinDirectory))
        {
            options.YtdlpBinDirectory = model.YtdlpBinDirectory;
        }

        if (model.YoutubeLiveProbeEnabled.HasValue)
        {
            options.YoutubeLiveProbeEnabled = model.YoutubeLiveProbeEnabled.Value;
        }

        if (model.YtdlpResolutionTimeoutSeconds.HasValue)
        {
            options.YtdlpResolutionTimeoutSeconds = model.YtdlpResolutionTimeoutSeconds.Value;
        }

        if (model.UseBrowserCookies.HasValue)
        {
            options.UseBrowserCookies = model.UseBrowserCookies.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.BrowserCookiesSource))
        {
            options.BrowserCookiesSource = model.BrowserCookiesSource;
        }

        if (model.YoutubeCookiesFilePath is not null)
        {
            options.YoutubeCookiesFilePath = string.IsNullOrWhiteSpace(model.YoutubeCookiesFilePath)
                ? null
                : model.YoutubeCookiesFilePath;
        }

        if (!string.IsNullOrWhiteSpace(model.YoutubeCookiesAlertFilePath))
        {
            options.YoutubeCookiesAlertFilePath = model.YoutubeCookiesAlertFilePath;
        }

        // Archive server credentials, like the SMTP ones, only ever come from the environment: one account per
        // medium (ARCHIVE_RADIO_FTP_*, ARCHIVE_TELEVISION_FTP_*, ARCHIVE_PRESS_FTP_*), plus the single account used
        // before (ARCHIVE_FTP_*) that any medium without its own keeps falling back to.
        options.ArchiveFtp = ReadArchiveCredentials("ARCHIVE_FTP");
        foreach (var (medium, prefix) in new[]
        {
            (ArchiveMedia.Radio, "ARCHIVE_RADIO_FTP"),
            (ArchiveMedia.Television, "ARCHIVE_TELEVISION_FTP"),
            (ArchiveMedia.Press, "ARCHIVE_PRESS_FTP"),
        })
        {
            if (ReadArchiveCredentials(prefix) is { } own)
            {
                options.ArchiveFtpByMedium[medium] = own;
            }
        }

        foreach (var (medium, variable) in new[]
        {
            (ArchiveMedia.Radio, "ARCHIVE_RADIO_PUBLIC_BASE_URL"),
            (ArchiveMedia.Television, "ARCHIVE_TELEVISION_PUBLIC_BASE_URL"),
            (ArchiveMedia.Press, "ARCHIVE_PRESS_PUBLIC_BASE_URL"),
        })
        {
            var address = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                options.ArchivePublicBaseUrlByMedium[medium] = address.Trim();
            }
        }

        var archivePublicBaseUrl = Environment.GetEnvironmentVariable("ARCHIVE_PUBLIC_BASE_URL");
        if (!string.IsNullOrWhiteSpace(archivePublicBaseUrl) && Uri.TryCreate(archivePublicBaseUrl.Trim(), UriKind.Absolute, out _))
        {
            options.ArchivePublicBaseUrl = archivePublicBaseUrl.Trim();
        }

        // Email channel for alerts; like the Firebase credentials, only ever read from the environment.
        var smtpHost = Environment.GetEnvironmentVariable("SMTP_HOST");
        var smtpFrom = Environment.GetEnvironmentVariable("SMTP_FROM");
        if (!string.IsNullOrWhiteSpace(smtpHost) && !string.IsNullOrWhiteSpace(smtpFrom))
        {
            var smtp = new MediaOpsCore.Modules.Alerting.Infrastructure.SmtpAlertOptions
            {
                Host = smtpHost.Trim(),
                From = smtpFrom.Trim(),
                User = Environment.GetEnvironmentVariable("SMTP_USER"),
                Password = Environment.GetEnvironmentVariable("SMTP_PASSWORD"),
            };

            if (int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var smtpPort))
            {
                smtp.Port = smtpPort;
            }

            if (bool.TryParse(Environment.GetEnvironmentVariable("SMTP_ENABLE_SSL"), out var smtpSsl))
            {
                smtp.EnableSsl = smtpSsl;
            }

            options.Smtp = smtp;
        }

        // MONGODB_URI matches the env var name apps/web-api already uses, so both point at one instance.
        var mongoConnectionStringEnv = Environment.GetEnvironmentVariable("MONGODB_URI");
        if (!string.IsNullOrWhiteSpace(mongoConnectionStringEnv))
        {
            options.MongoConnectionString = mongoConnectionStringEnv.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(model.MongoConnectionString))
        {
            options.MongoConnectionString = model.MongoConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(model.MongoConfigDatabaseName))
        {
            options.MongoConfigDatabaseName = model.MongoConfigDatabaseName;
        }

        if (!string.IsNullOrWhiteSpace(model.MongoMonitoringDatabaseName))
        {
            options.MongoMonitoringDatabaseName = model.MongoMonitoringDatabaseName;
        }

        if (!string.IsNullOrWhiteSpace(model.MongoAlertCollectionName))
        {
            options.MongoAlertCollectionName = model.MongoAlertCollectionName;
        }

        if (model.WhatsAppSidecarPort.HasValue)
        {
            options.WhatsAppSidecarPort = model.WhatsAppSidecarPort.Value;
        }

        if (model.WhatsAppSidecarStartupTimeoutSeconds.HasValue)
        {
            options.WhatsAppSidecarStartupTimeoutSeconds = model.WhatsAppSidecarStartupTimeoutSeconds.Value;
        }

        if (!string.IsNullOrWhiteSpace(model.WhatsAppAuthStatePath))
        {
            options.WhatsAppAuthStatePath = model.WhatsAppAuthStatePath;
        }

        if (model.DailyHeadlinesEnabled.HasValue)
        {
            options.DailyHeadlinesEnabled = model.DailyHeadlinesEnabled.Value;
        }

        if (TimeOnly.TryParseExact(model.DailyHeadlinesTime, "HH:mm", out _))
        {
            options.DailyHeadlinesTime = model.DailyHeadlinesTime!;
        }

        if (model.WebNewsEnabled.HasValue)
        {
            options.WebNewsEnabled = model.WebNewsEnabled.Value;
        }

        if (model.WebNewsKeywordsPerQuery.HasValue)
        {
            options.WebNewsKeywordsPerQuery = model.WebNewsKeywordsPerQuery.Value;
        }

        if (model.WebNewsMaxConcurrency.HasValue)
        {
            options.WebNewsMaxConcurrency = model.WebNewsMaxConcurrency.Value;
        }

        if (model.WebNewsRequestTimeoutSeconds.HasValue)
        {
            options.WebNewsRequestTimeoutSeconds = model.WebNewsRequestTimeoutSeconds.Value;
        }

        if (model.WebNewsMaxFailedAttempts.HasValue)
        {
            options.WebNewsMaxFailedAttempts = model.WebNewsMaxFailedAttempts.Value;
        }

        return NormalizeConfiguredPaths(options, applicationRoot, configDirectory);
    }

    private static OperationsWorkerOptions NormalizeConfiguredPaths(
        OperationsWorkerOptions options,
        string applicationRoot,
        string? configDirectory)
    {
        options.PluginProfilesFilePath = ResolveConfiguredPath(options.PluginProfilesFilePath, applicationRoot, configDirectory);
        options.StageFilesystemRootPath = ResolveConfiguredPath(options.StageFilesystemRootPath, applicationRoot, configDirectory);
        options.YtdlpBinDirectory = ResolveConfiguredPath(options.YtdlpBinDirectory, applicationRoot, configDirectory);
        options.HeadlessBrowserPath = ResolveConfiguredPath(options.HeadlessBrowserPath, applicationRoot, configDirectory);
        options.YoutubeCookiesAlertFilePath = ResolveConfiguredPath(options.YoutubeCookiesAlertFilePath, applicationRoot, configDirectory);
        options.WhatsAppAuthStatePath = ResolveConfiguredPath(options.WhatsAppAuthStatePath, applicationRoot, configDirectory);

        if (!string.IsNullOrWhiteSpace(options.YoutubeCookiesFilePath))
        {
            options.YoutubeCookiesFilePath = ResolveConfiguredPath(options.YoutubeCookiesFilePath, applicationRoot, configDirectory);
        }

        return options;
    }

    private static string ResolveConfigPath(string configPath)
    {
        if (Path.IsPathRooted(configPath))
        {
            return Path.GetFullPath(configPath);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, configPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(configPath, Directory.GetCurrentDirectory());
    }

    private static string ResolveApplicationRoot(string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return Directory.GetCurrentDirectory();
        }

        var directoryName = Path.GetFileName(configDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(directoryName, StageDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(configDirectory);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return configDirectory;
    }

    private static string ResolveConfiguredPath(string configuredPath, string applicationRoot, string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var appRootCandidate = Path.GetFullPath(Path.Combine(applicationRoot, configuredPath));
        if (Path.Exists(appRootCandidate))
        {
            return appRootCandidate;
        }

        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            var configDirectoryCandidate = Path.GetFullPath(Path.Combine(configDirectory, configuredPath));
            if (Path.Exists(configDirectoryCandidate))
            {
                return configDirectoryCandidate;
            }
        }

        return appRootCandidate;
    }
}

