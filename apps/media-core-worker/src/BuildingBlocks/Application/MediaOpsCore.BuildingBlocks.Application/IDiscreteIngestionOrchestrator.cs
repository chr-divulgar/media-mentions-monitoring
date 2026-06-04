namespace MediaOpsCore.BuildingBlocks.Application;

public interface IDiscreteIngestionOrchestrator
{
    Task ExecuteCycleAsync(CancellationToken cancellationToken = default);
}