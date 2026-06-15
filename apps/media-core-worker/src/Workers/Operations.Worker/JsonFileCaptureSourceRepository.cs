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
}
