using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

public static class OperationsWorkerOptionsLoader
{
    private const string DefaultConfigPath = "stage/worker-options.json";

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
        string? YoutubeCookiesFilePath,
        string? YoutubeCookiesAlertFilePath,
        FirebaseDatabaseLoaderSection? FirebaseDatabase);

    private sealed record FirebaseDatabaseLoaderSection(
        string? BaseUrl,
        string? PlatformsPath,
        string? AuthToken,
        int? RequestTimeoutSeconds);

    public static OperationsWorkerOptions Load(string? configPath = null)
    {
        var options = new OperationsWorkerOptions();
        var path = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath;

        if (!File.Exists(path))
        {
            return options;
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return options;
        }

        var model = JsonSerializer.Deserialize<WorkerOptionsFileModel>(json, SerializerOptions);
        if (model is null)
        {
            return options;
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

        if (model.FirebaseDatabase is { } fb && !string.IsNullOrWhiteSpace(fb.BaseUrl))
        {
            options.FirebaseDatabase = new FirebaseCaptureSourceRepositoryOptions
            {
                BaseUrl = fb.BaseUrl.Trim(),
                PlatformsPath = string.IsNullOrWhiteSpace(fb.PlatformsPath) ? "platforms" : fb.PlatformsPath.Trim('/'),
                AuthToken = string.IsNullOrWhiteSpace(fb.AuthToken) ? null : fb.AuthToken,
                RequestTimeoutSeconds = fb.RequestTimeoutSeconds ?? 15
            };
        }

        return options;
    }
}

