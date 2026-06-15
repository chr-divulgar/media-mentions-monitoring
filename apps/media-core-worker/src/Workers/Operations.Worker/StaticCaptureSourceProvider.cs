using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class StaticCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly OperationsWorkerOptions options;
    private readonly ICaptureSourceRepository sourceRepository;
    private readonly object syncRoot = new();
    private IReadOnlyList<CaptureSource>? resolvedSources;

    // Used only by the Persist* methods that write runtime state back to the local JSON file.
    private sealed record CaptureSourceFileItem(string SourceId, string Platform, string Media, string StreamUrl, string? PrimaryUrl, string? Country, IReadOnlyList<string>? FallbackStreamUrls = null, bool? Excluded = null);

    public StaticCaptureSourceProvider(OperationsWorkerOptions options, ICaptureSourceRepository sourceRepository)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.sourceRepository = sourceRepository ?? throw new ArgumentNullException(nameof(sourceRepository));
    }

    public Task<IReadOnlyList<CaptureSource>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        var sources = ListResolvedSources();
        var mediaScoped = ApplyContinuousMediaFilter(sources);
        var filtered = ApplyCanaryFilter(mediaScoped);

        return Task.FromResult<IReadOnlyList<CaptureSource>>(filtered);
    }

    public Task<IReadOnlyList<CaptureSource>> ListConfiguredSourcesAsync(CancellationToken cancellationToken = default)
    {
        return sourceRepository.ListAllAsync(cancellationToken);
    }

    public void SetResolvedSources(IReadOnlyList<CaptureSource> sources)
    {
        lock (syncRoot)
        {
            resolvedSources = sources.ToArray();
        }
    }

    public IReadOnlyList<CaptureSource> ListResolvedSources()
    {
        lock (syncRoot)
        {
            if (resolvedSources is null)
            {
                // Excluded sources are loaded into configuredSources (visible for reconciliation)
                // but must not start capturing — skip them in the initial resolved set.
                resolvedSources = sourceRepository
                    .ListAllAsync()
                    .GetAwaiter().GetResult()
                    .Where(s => !s.IsExcluded)
                    .ToArray();
            }

            return resolvedSources.ToArray();
        }
    }

    public bool AddOrUpdateResolvedSource(CaptureSource source)
    {
        lock (syncRoot)
        {
            var current = (resolvedSources ?? sourceRepository.ListAllAsync().GetAwaiter().GetResult()).ToList();
            var index = current.FindIndex(item => string.Equals(item.SourceId, source.SourceId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                var existing = current[index];
                if (string.Equals(existing.StreamUrl, source.StreamUrl, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.PrimaryUrl, source.PrimaryUrl, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Platform, source.Platform, StringComparison.Ordinal)
                    && string.Equals(existing.Media, source.Media, StringComparison.Ordinal)
                    && existing.FallbackStreamUrls.SequenceEqual(source.FallbackStreamUrls, StringComparer.OrdinalIgnoreCase))
                {
                    resolvedSources = current.ToArray();
                    return false;
                }

                current[index] = source;
                resolvedSources = current.ToArray();
                return true;
            }

            current.Add(source);
            resolvedSources = current.ToArray();
            return true;
        }
    }

    public bool RemoveResolvedSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        lock (syncRoot)
        {
            var current = (resolvedSources ?? sourceRepository.ListAllAsync().GetAwaiter().GetResult()).ToList();
            var removed = current.RemoveAll(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            resolvedSources = current.ToArray();
            return true;
        }
    }

    public Task<bool> PersistStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(streamUrl))
        {
            return Task.FromResult(false);
        }

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, JsonFileCaptureSourceRepository.SerializerOptions);
        if (items is null || items.Count == 0)
        {
            return Task.FromResult(false);
        }

        var sourceIndex = items.FindIndex(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return Task.FromResult(false);
        }

        if (string.Equals(items[sourceIndex].StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        items[sourceIndex] = items[sourceIndex] with { StreamUrl = streamUrl };

        var updatedJson = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        File.WriteAllText(options.CaptureSourcesFilePath, updatedJson);
        return Task.FromResult(true);
    }

    public Task<bool> PersistFallbackStreamUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || fallbackStreamUrls is null)
        {
            return Task.FromResult(false);
        }

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, JsonFileCaptureSourceRepository.SerializerOptions);
        if (items is null || items.Count == 0)
        {
            return Task.FromResult(false);
        }

        var sourceIndex = items.FindIndex(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return Task.FromResult(false);
        }

        var cleaned = fallbackStreamUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existing = items[sourceIndex].FallbackStreamUrls;
        if (existing is not null && existing.SequenceEqual(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        items[sourceIndex] = items[sourceIndex] with { FallbackStreamUrls = cleaned.Length > 0 ? cleaned : null };

        var updatedJson = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        File.WriteAllText(options.CaptureSourcesFilePath, updatedJson);
        return Task.FromResult(true);
    }

    public Task<bool> PersistExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return Task.FromResult(false);
        }

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, JsonFileCaptureSourceRepository.SerializerOptions);
        if (items is null || items.Count == 0)
        {
            return Task.FromResult(false);
        }

        var sourceIndex = items.FindIndex(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return Task.FromResult(false);
        }

        var current = items[sourceIndex].Excluded ?? false;
        if (current == excluded)
        {
            return Task.FromResult(false);
        }

        items[sourceIndex] = items[sourceIndex] with { Excluded = excluded ? true : null };

        var updatedJson = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        File.WriteAllText(options.CaptureSourcesFilePath, updatedJson);
        return Task.FromResult(true);
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
