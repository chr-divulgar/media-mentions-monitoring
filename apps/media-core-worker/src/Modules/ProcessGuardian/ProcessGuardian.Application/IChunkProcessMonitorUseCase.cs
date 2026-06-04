namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public interface IChunkProcessMonitorUseCase
{
    Task<ChunkProcessMonitorResult> ExecuteAsync(CancellationToken cancellationToken = default);
}