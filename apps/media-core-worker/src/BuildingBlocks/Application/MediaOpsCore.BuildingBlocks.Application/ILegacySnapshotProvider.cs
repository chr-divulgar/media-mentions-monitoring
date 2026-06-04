namespace MediaOpsCore.BuildingBlocks.Application;

public interface ILegacySnapshotProvider
{
    Task<IReadOnlyList<LegacyCollectionSnapshot>> GetCollectionSnapshotsAsync(CancellationToken cancellationToken = default);
}