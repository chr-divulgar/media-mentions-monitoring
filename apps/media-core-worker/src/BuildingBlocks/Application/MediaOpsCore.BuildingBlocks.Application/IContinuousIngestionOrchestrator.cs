namespace MediaOpsCore.BuildingBlocks.Application;

public interface IContinuousIngestionOrchestrator
{
    Task ExecuteCycleAsync(CancellationToken cancellationToken = default);
}