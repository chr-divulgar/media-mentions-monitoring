using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class JsonPluginProfileProvider : IPluginProfileProvider
{
    private readonly OperationsWorkerOptions options;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record JsonPluginProfileItem(
        string PluginId,
        string Media,
        string? Platform,
        string? IngestionMode,
        int? FlacWindowDurationSeconds,
        int? OpusFlushIntervalSeconds,
        int? OpusRotationIntervalHours);

    public JsonPluginProfileProvider(OperationsWorkerOptions options)
    {
        this.options = options;
    }

    public Task<IReadOnlyList<PluginProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.PluginProfilesFilePath))
        {
            return Task.FromResult<IReadOnlyList<PluginProfile>>(Array.Empty<PluginProfile>());
        }

        if (!File.Exists(options.PluginProfilesFilePath))
        {
            return Task.FromResult<IReadOnlyList<PluginProfile>>(Array.Empty<PluginProfile>());
        }

        var json = File.ReadAllText(options.PluginProfilesFilePath);
        var items = JsonSerializer.Deserialize<List<JsonPluginProfileItem>>(json, SerializerOptions);
        if (items is null || items.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PluginProfile>>(Array.Empty<PluginProfile>());
        }

        var profiles = items
            .Select(item => new PluginProfile(
                item.PluginId,
                item.Media,
                item.Platform,
                ParseIngestionMode(item.IngestionMode),
                TimeSpan.FromSeconds(item.FlacWindowDurationSeconds.GetValueOrDefault(options.DefaultFlacWindowDurationSeconds)),
                TimeSpan.FromSeconds(item.OpusFlushIntervalSeconds.GetValueOrDefault(options.DefaultOpusFlushIntervalSeconds)),
                TimeSpan.FromHours(item.OpusRotationIntervalHours.GetValueOrDefault(options.DefaultOpusRotationIntervalHours))))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PluginProfile>>(profiles);
    }

    private static IngestionMode ParseIngestionMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "discrete" => IngestionMode.Discrete,
            _ => IngestionMode.Continuous
        };
    }
}

