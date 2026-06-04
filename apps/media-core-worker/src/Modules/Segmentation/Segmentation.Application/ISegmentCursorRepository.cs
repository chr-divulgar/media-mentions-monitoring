namespace MediaOpsCore.Modules.Segmentation.Application;

public interface ISegmentCursorRepository
{
    Task<DateTimeOffset?> GetLastProcessedAtAsync(string tenantId, CancellationToken cancellationToken = default);

    Task SaveLastProcessedAtAsync(string tenantId, DateTimeOffset lastProcessedAtUtc, CancellationToken cancellationToken = default);
}