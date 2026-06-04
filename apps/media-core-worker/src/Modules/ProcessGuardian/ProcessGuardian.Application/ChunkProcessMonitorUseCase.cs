namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed class ChunkProcessMonitorUseCase : IChunkProcessMonitorUseCase
{
    private readonly IProcessStateRepository processStateRepository;
    private readonly IProcessInspector processInspector;
    private readonly Func<DateTimeOffset> utcNow;

    public ChunkProcessMonitorUseCase(
        IProcessStateRepository processStateRepository,
        IProcessInspector processInspector,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.processStateRepository = processStateRepository;
        this.processInspector = processInspector;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ChunkProcessMonitorResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var states = await processStateRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var chunkStates = states
            .Where(state => string.Equals(state.ProcessType, "chunk", StringComparison.OrdinalIgnoreCase) && string.Equals(state.Status, "Active", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var orphansDetected = 0;
        var orphansStopped = 0;

        foreach (var state in chunkStates)
        {
            if (!IsOrphan(state))
            {
                continue;
            }

            orphansDetected++;
            var wasRunning = processInspector.IsRunning(state.ProcessId);
            if (!wasRunning)
            {
                continue;
            }

            if (!processInspector.TryStop(state.ProcessId))
            {
                continue;
            }

            orphansStopped++;
            var updatedState = state with
            {
                EndedAtUtc = utcNow(),
                Status = "OrphanStopped"
            };
            await processStateRepository.UpsertAsync(updatedState, cancellationToken).ConfigureAwait(false);
        }

        return new ChunkProcessMonitorResult(orphansDetected, orphansStopped);
    }

    private static bool IsOrphan(MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence.ProcessState state)
    {
        if (string.IsNullOrWhiteSpace(state.SourceFilePath))
        {
            return true;
        }

        return !File.Exists(state.SourceFilePath);
    }
}