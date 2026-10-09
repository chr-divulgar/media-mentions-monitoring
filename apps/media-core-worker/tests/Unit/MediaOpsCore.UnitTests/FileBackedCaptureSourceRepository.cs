using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.UnitTests;

/// <summary>
/// Test support: a capture source catalogue seeded from a small JSON file (the shape the tests
/// write: sourceId, platform, media, streamUrl, primaryUrl, country, fallbackStreamUrls, excluded)
/// and kept in memory. The product's catalogue is MongoCaptureSourceRepository; this only spares
/// the provider/startup/capture tests from needing a database.
/// </summary>
internal sealed class FileBackedCaptureSourceRepository(string path) : ICaptureSourceRepository
{
    private sealed record Item(
        string SourceId,
        string Platform,
        string Media,
        string StreamUrl,
        string? PrimaryUrl,
        string? Country,
        IReadOnlyList<string>? FallbackStreamUrls = null,
        bool? Excluded = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private List<CaptureSource>? sources;

    private List<CaptureSource> Load() => sources ??= (JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(path), Json) ?? [])
        .Select(item => new CaptureSource(
            item.SourceId,
            "global-ingestion",
            item.Platform,
            item.Media,
            item.StreamUrl,
            item.PrimaryUrl,
            item.Country,
            fallbackStreamUrls: item.FallbackStreamUrls,
            isExcluded: item.Excluded ?? false))
        .ToList();

    public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CaptureSource>>(Load().ToArray());

    public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
        Replace(sourceId, source => source.WithStreamUrl(streamUrl));

    public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
        Replace(sourceId, source => source.WithFallbackStreamUrls(fallbackStreamUrls));

    public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
        Replace(sourceId, source => source.WithExcluded(excluded));

    private Task<bool> Replace(string sourceId, Func<CaptureSource, CaptureSource> change)
    {
        var all = Load();
        var index = all.FindIndex(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        all[index] = change(all[index]);
        return Task.FromResult(true);
    }
}
