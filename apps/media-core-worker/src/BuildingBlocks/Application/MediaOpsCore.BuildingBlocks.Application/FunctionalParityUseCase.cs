namespace MediaOpsCore.BuildingBlocks.Application;

public sealed class FunctionalParityUseCase : IFunctionalParityUseCase
{
    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ILegacySnapshotProvider legacySnapshotProvider;
    private readonly FunctionalParityOptions options;
    private readonly Func<DateTimeOffset> utcNow;

    public FunctionalParityUseCase(
        IMonitoringArtifactRepository monitoringArtifactRepository,
        ILegacySnapshotProvider legacySnapshotProvider,
        FunctionalParityOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.legacySnapshotProvider = legacySnapshotProvider;
        this.options = options;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FunctionalParityReport> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            throw new InvalidOperationException("TenantId cannot be empty.");
        }

        var currentArtifacts = await monitoringArtifactRepository.ListByTenantAsync(options.TenantId, cancellationToken).ConfigureAwait(false);
        var legacySnapshots = await legacySnapshotProvider.GetCollectionSnapshotsAsync(cancellationToken).ConfigureAwait(false);

        var currentByCollection = currentArtifacts
            .GroupBy(artifact => artifact.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var legacyByCollection = legacySnapshots
            .GroupBy(snapshot => snapshot.Collection, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.Ordinal);

        var collections = legacyByCollection.Keys
            .Union(currentByCollection.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        var parityByCollection = collections
            .Select(collection => BuildCollectionResult(collection, legacyByCollection, currentByCollection))
            .ToArray();

        var overallParityPercent = parityByCollection.Length == 0
            ? 100
            : parityByCollection.Average(item => item.ParityPercent);

        var meetsThreshold = overallParityPercent >= options.MinimumParityPercent;

        return new FunctionalParityReport(
            ComparedAtUtc: utcNow(),
            Collections: parityByCollection,
            OverallParityPercent: overallParityPercent,
            MeetsThreshold: meetsThreshold);
    }

    private static CollectionParityResult BuildCollectionResult(
        string collection,
        IReadOnlyDictionary<string, int> legacyByCollection,
        IReadOnlyDictionary<string, int> currentByCollection)
    {
        var legacyCount = legacyByCollection.TryGetValue(collection, out var legacy) ? legacy : 0;
        var currentCount = currentByCollection.TryGetValue(collection, out var current) ? current : 0;
        var maxCount = Math.Max(legacyCount, currentCount);

        var parityPercent = maxCount == 0
            ? 100
            : Math.Round((Math.Min(legacyCount, currentCount) / (double)maxCount) * 100, 2);

        return new CollectionParityResult(collection, legacyCount, currentCount, parityPercent);
    }
}