using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class JsonLegacySnapshotProvider : ILegacySnapshotProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly OperationsWorkerOptions options;

    public JsonLegacySnapshotProvider(OperationsWorkerOptions options)
    {
        this.options = options;
    }

    public async Task<IReadOnlyList<LegacyCollectionSnapshot>> GetCollectionSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.LegacySnapshotFilePath) || !File.Exists(options.LegacySnapshotFilePath))
        {
            return new[]
            {
                new LegacyCollectionSnapshot("capture", 0),
                new LegacyCollectionSnapshot("segment", 0)
            };
        }

        await using var stream = File.OpenRead(options.LegacySnapshotFilePath);
        var snapshots = await JsonSerializer.DeserializeAsync<List<LegacyCollectionSnapshot>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);

        return snapshots?.ToArray() ?? Array.Empty<LegacyCollectionSnapshot>();
    }
}