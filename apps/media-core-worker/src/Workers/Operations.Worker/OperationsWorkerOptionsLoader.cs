using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

public static class OperationsWorkerOptionsLoader
{
    private const string DefaultConfigPath = "stage/worker-options.json";
    private const string StageDirectoryName = "stage";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record WorkerOptionsFileModel(
        int? SegmentationIntervalSeconds,
        int? DiscreteWorkerIntervalSeconds,
        string? CaptureSourcesFilePath,
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
        bool? UseBrowserCookies,
        string? BrowserCookiesSource,
        string? YoutubeCookiesFilePath,
        string? YoutubeCookiesAlertFilePath,
        string? MongoConnectionString,
        string? MongoConfigDatabaseName,
        string? MongoMonitoringDatabaseName,
        string? MongoAlertCollectionName);

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

        if (!string.IsNullOrWhiteSpace(model.CaptureSourcesFilePath))
        {
            options.CaptureSourcesFilePath = model.CaptureSourcesFilePath;
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

        // Same service-account variables apps/web-api already uses for Firestore (FIREBASE_*) —
        // secrets only ever come from the environment, never from worker-options.json.
        var firebaseProjectId = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID");
        var firebaseClientEmail = Environment.GetEnvironmentVariable("FIREBASE_CLIENT_EMAIL");
        var firebasePrivateKey = Environment.GetEnvironmentVariable("FIREBASE_PRIVATE_KEY");
        var firebaseCollectionPath = Environment.GetEnvironmentVariable("FIREBASE_FIRESTORE_COLLECTION");
        var firebaseTimeoutSecondsStr = Environment.GetEnvironmentVariable("FIREBASE_REQUEST_TIMEOUT_SECONDS");

        if (!string.IsNullOrWhiteSpace(firebaseProjectId) &&
            !string.IsNullOrWhiteSpace(firebaseClientEmail) &&
            !string.IsNullOrWhiteSpace(firebasePrivateKey))
        {
            var timeoutSeconds = 15;
            if (!string.IsNullOrWhiteSpace(firebaseTimeoutSecondsStr) && int.TryParse(firebaseTimeoutSecondsStr, out var envTimeout))
            {
                timeoutSeconds = envTimeout;
            }

            options.Firestore = new FirestoreCaptureSourceRepositoryOptions
            {
                ProjectId = firebaseProjectId.Trim(),
                ClientEmail = firebaseClientEmail.Trim(),
                PrivateKeyPem = firebasePrivateKey,
                CollectionPath = string.IsNullOrWhiteSpace(firebaseCollectionPath) ? "platforms" : firebaseCollectionPath.Trim('/'),
                RequestTimeoutSeconds = timeoutSeconds
            };
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

        return NormalizeConfiguredPaths(options, applicationRoot, configDirectory);
    }

    private static OperationsWorkerOptions NormalizeConfiguredPaths(
        OperationsWorkerOptions options,
        string applicationRoot,
        string? configDirectory)
    {
        options.CaptureSourcesFilePath = ResolveConfiguredPath(options.CaptureSourcesFilePath, applicationRoot, configDirectory);
        options.PluginProfilesFilePath = ResolveConfiguredPath(options.PluginProfilesFilePath, applicationRoot, configDirectory);
        options.StageFilesystemRootPath = ResolveConfiguredPath(options.StageFilesystemRootPath, applicationRoot, configDirectory);
        options.YtdlpBinDirectory = ResolveConfiguredPath(options.YtdlpBinDirectory, applicationRoot, configDirectory);
        options.YoutubeCookiesAlertFilePath = ResolveConfiguredPath(options.YoutubeCookiesAlertFilePath, applicationRoot, configDirectory);

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

