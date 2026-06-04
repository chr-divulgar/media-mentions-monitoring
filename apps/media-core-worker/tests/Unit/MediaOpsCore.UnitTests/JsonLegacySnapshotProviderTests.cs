using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class JsonLegacySnapshotProviderTests
{
    [Fact]
    public async Task GetCollectionSnapshotsAsync_should_load_snapshot_file_when_present()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"legacy-snapshot-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new[]
            {
                new LegacyCollectionSnapshot("capture", 10),
                new LegacyCollectionSnapshot("segment", 8)
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));
            var provider = new JsonLegacySnapshotProvider(new OperationsWorkerOptions
            {
                LegacySnapshotFilePath = tempFilePath
            });

            var snapshots = await provider.GetCollectionSnapshotsAsync();

            Assert.Equal(2, snapshots.Count);
            Assert.Contains(snapshots, snapshot => snapshot.Collection == "capture" && snapshot.Count == 10);
            Assert.Contains(snapshots, snapshot => snapshot.Collection == "segment" && snapshot.Count == 8);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }
}