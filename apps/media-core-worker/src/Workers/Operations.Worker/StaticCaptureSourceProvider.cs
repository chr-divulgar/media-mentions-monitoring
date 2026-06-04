using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StaticCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly OperationsWorkerOptions options;
    private const string GlobalIngestionScopeId = "global-ingestion";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record CaptureSourceFileItem(string SourceId, string Platform, string Media, string StreamUrl);

    public StaticCaptureSourceProvider(OperationsWorkerOptions options)
    {
        this.options = options;
    }

    public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        var sources = LoadSources();
        var mediaScoped = ApplyContinuousMediaFilter(sources);
        var filtered = ApplyCanaryFilter(mediaScoped);

        return Task.FromResult<IReadOnlyList<CaptureSource>>(filtered);
    }

    private IReadOnlyList<CaptureSource> ApplyContinuousMediaFilter(IReadOnlyList<CaptureSource> sources)
    {
        var allowList = ParseAllowList(options.ContinuousMediaAllowList);
        if (allowList.Count == 0)
        {
            return sources;
        }

        return sources
            .Where(source => allowList.Contains(source.Media))
            .ToArray();
    }

    private IReadOnlyList<CaptureSource> LoadSources()
    {
        if (string.IsNullOrWhiteSpace(options.CaptureSourcesFilePath))
        {
            throw new InvalidOperationException("CaptureSourcesFilePath is required.");
        }

        if (!File.Exists(options.CaptureSourcesFilePath))
        {
            throw new FileNotFoundException("Capture sources file not found.", options.CaptureSourcesFilePath);
        }

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, SerializerOptions);
        if (items is null || items.Count == 0)
        {
            throw new InvalidOperationException("Capture sources file is empty or invalid.");
        }

        return items
            .Select(item => new CaptureSource(item.SourceId, GlobalIngestionScopeId, item.Platform, item.Media, item.StreamUrl))
            .ToArray();
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