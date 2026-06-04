namespace MediaOpsCore.BuildingBlocks.Application;

public sealed record CollectionParityResult(string Collection, int LegacyCount, int CurrentCount, double ParityPercent);