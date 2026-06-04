using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StaticCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly OperationsWorkerOptions options;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record CaptureSourceFileItem(string SourceId, string TenantId, string Platform, string Media, string StreamUrl);

    public StaticCaptureSourceProvider(OperationsWorkerOptions options)
    {
        this.options = options;
    }

    public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        var sources = LoadSources();
        var filtered = ApplyCanaryFilter(sources);

        return Task.FromResult<IReadOnlyList<CaptureSource>>(filtered);
    }

    private IReadOnlyList<CaptureSource> LoadSources()
    {
        if (!string.IsNullOrWhiteSpace(options.CaptureSourcesFilePath) && File.Exists(options.CaptureSourcesFilePath))
        {
            var json = File.ReadAllText(options.CaptureSourcesFilePath);
            var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, SerializerOptions);
            if (items is not null && items.Count > 0)
            {
                return items
                    .Select(item => new CaptureSource(item.SourceId, item.TenantId, item.Platform, item.Media, item.StreamUrl))
                    .ToArray();
            }
        }

        return new[]
        {
            new CaptureSource(
                options.CaptureSourceId,
                options.TenantId,
                options.CapturePlatform,
                options.CaptureMedia,
                options.CaptureStreamUrl)
        };
    }

    private IReadOnlyList<CaptureSource> ApplyCanaryFilter(IReadOnlyList<CaptureSource> sources)
    {
        if (!options.EnableCanaryMode || sources.Count == 0)
        {
            return sources;
        }

        var allowList = ParseAllowList(options.CanaryPlatformAllowList);
        var sourcePool = allowList.Count == 0
            ? sources
            : sources.Where(source => allowList.Contains(source.Platform)).ToArray();

        if (sourcePool.Count == 0)
        {
            return Array.Empty<CaptureSource>();
        }

        var distinctPlatforms = sourcePool
            .Select(source => source.Platform)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(platform => platform, StringComparer.Ordinal)
            .ToArray();

        var percent = Math.Clamp(options.CanaryPlatformPercent, options.CanaryPlatformMinPercent, options.CanaryPlatformMaxPercent);
        var platformTarget = Math.Max(1, (int)Math.Ceiling(distinctPlatforms.Length * (percent / 100.0)));
        var selectedPlatforms = distinctPlatforms.Take(platformTarget).ToHashSet(StringComparer.Ordinal);

        return sourcePool
            .Where(source => selectedPlatforms.Contains(source.Platform))
            .ToArray();
    }

    private static HashSet<string> ParseAllowList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}