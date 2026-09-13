using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads the capture source catalog from the local JSON file at
/// <see cref="OperationsWorkerOptions.CaptureSourcesFilePath"/>.
/// Used as the primary source when Firebase is not configured, and as the fallback otherwise.
/// </summary>
public sealed class JsonFileCaptureSourceRepository : ICaptureSourceRepository
{
    internal const string GlobalIngestionScopeId = "global-ingestion";

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal sealed record CaptureSourceFileItem(
        string SourceId,
        string Platform,
        string Media,
        string StreamUrl,
        string? PrimaryUrl,
        string? Country,
        IReadOnlyList<string>? FallbackStreamUrls = null,
        bool? Excluded = null);

    private readonly OperationsWorkerOptions options;

    public JsonFileCaptureSourceRepository(OperationsWorkerOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.CaptureSourcesFilePath))
            throw new InvalidOperationException("CaptureSourcesFilePath is required.");

        if (!File.Exists(options.CaptureSourcesFilePath))
            throw new FileNotFoundException("Capture sources file not found.", options.CaptureSourcesFilePath);

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, SerializerOptions);
        if (items is null || items.Count == 0)
            throw new InvalidOperationException("Capture sources file is empty or invalid.");

        IReadOnlyList<CaptureSource> result = items
            .Select(item => new CaptureSource(
                item.SourceId,
                GlobalIngestionScopeId,
                item.Platform,
                item.Media,
                item.StreamUrl,
                item.PrimaryUrl,
                item.Country,
                fallbackStreamUrls: item.FallbackStreamUrls,
                isExcluded: item.Excluded ?? false))
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(streamUrl))
            return Task.FromResult(false);

        var items = LoadFileItems();
        if (items.Count == 0)
            return Task.FromResult(false);

        var sourceIndex = items.FindIndex(item =>
            string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
            return Task.FromResult(false);

        if (string.Equals(items[sourceIndex].StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        items[sourceIndex] = items[sourceIndex] with { StreamUrl = streamUrl };
        SaveFileItems(items, ignoreNulls: false);
        return Task.FromResult(true);
    }

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceId) || fallbackStreamUrls is null)
            return Task.FromResult(false);

        var items = LoadFileItems();
        if (items.Count == 0)
            return Task.FromResult(false);

        var sourceIndex = items.FindIndex(item =>
            string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
            return Task.FromResult(false);

        var cleaned = fallbackStreamUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existing = items[sourceIndex].FallbackStreamUrls;
        if (existing is not null && existing.SequenceEqual(cleaned, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(false);

        if ((existing is null || existing.Count == 0) && cleaned.Length == 0)
            return Task.FromResult(false);

        items[sourceIndex] = items[sourceIndex] with { FallbackStreamUrls = cleaned.Length > 0 ? cleaned : null };
        SaveFileItems(items, ignoreNulls: false);
        return Task.FromResult(true);
    }

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceId))
            return Task.FromResult(false);

        var items = LoadFileItems();
        if (items.Count == 0)
            return Task.FromResult(false);

        var sourceIndex = items.FindIndex(item =>
            string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
            return Task.FromResult(false);

        var current = items[sourceIndex].Excluded ?? false;
        if (current == excluded)
            return Task.FromResult(false);

        items[sourceIndex] = items[sourceIndex] with { Excluded = excluded ? true : null };
        SaveFileItems(items, ignoreNulls: true);
        return Task.FromResult(true);
    }

    private List<CaptureSourceFileItem> LoadFileItems()
    {
        if (string.IsNullOrWhiteSpace(options.CaptureSourcesFilePath))
            throw new InvalidOperationException("CaptureSourcesFilePath is required.");

        if (!File.Exists(options.CaptureSourcesFilePath))
            throw new FileNotFoundException("Capture sources file not found.", options.CaptureSourcesFilePath);

        var json = File.ReadAllText(options.CaptureSourcesFilePath);
        var items = JsonSerializer.Deserialize<List<CaptureSourceFileItem>>(json, SerializerOptions);
        if (items is null)
            throw new InvalidOperationException("Capture sources file is invalid.");

        return items;
    }

    private void SaveFileItems(List<CaptureSourceFileItem> items, bool ignoreNulls)
    {
        var updatedJson = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = ignoreNulls
                ? System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                : System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });

        File.WriteAllText(options.CaptureSourcesFilePath, updatedJson);
    }
}
