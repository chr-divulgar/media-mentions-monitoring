namespace MediaOpsCore.BuildingBlocks.Application;

public sealed record FunctionalParityReport(
    DateTimeOffset ComparedAtUtc,
    IReadOnlyList<CollectionParityResult> Collections,
    double OverallParityPercent,
    bool MeetsThreshold);