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
        int? HeartbeatIntervalSeconds,
        int? DiscreteWorkerIntervalSeconds,
        string? CaptureSourcesFilePath,
        string? PluginProfilesFilePath,
        string? ContinuousMediaAllowList,
        bool? EnableCanaryMode,
        int? CanaryPlatformPercent,
        int? CanaryPlatformMinPercent,
        int? CanaryPlatformMaxPercent,
        string? CanaryPlatformAllowList,
        string? StageFilesystemRootPath,
        int? SegmentDurationSeconds,
        int? ProcessGuardianTimeoutSeconds,
        int? ProcessGuardianRestartCommandTimeoutSeconds);

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

        if (model.HeartbeatIntervalSeconds.HasValue)
        {
            options.HeartbeatInterval = TimeSpan.FromSeconds(model.HeartbeatIntervalSeconds.Value);
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

        if (model.ProcessGuardianTimeoutSeconds.HasValue)
        {
            options.ProcessGuardianTimeout = TimeSpan.FromSeconds(model.ProcessGuardianTimeoutSeconds.Value);
        }

        if (model.ProcessGuardianRestartCommandTimeoutSeconds.HasValue)
        {
            options.ProcessGuardianRestartCommandTimeout = TimeSpan.FromSeconds(model.ProcessGuardianRestartCommandTimeoutSeconds.Value);
        }

        return options;
    }
}
