using MediaOpsCore.Modules.Segmentation.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class InMemorySegmentCursorRepository : ISegmentCursorRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> cursors = new(StringComparer.Ordinal);

    public Task<DateTimeOffset?> GetLastProcessedAtAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            return Task.FromResult(cursors.TryGetValue(tenantId, out var value) ? value : (DateTimeOffset?)null);
        }
    }

    public Task SaveLastProcessedAtAsync(string tenantId, DateTimeOffset lastProcessedAtUtc, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            cursors[tenantId] = lastProcessedAtUtc;
        }

        return Task.CompletedTask;
    }
}