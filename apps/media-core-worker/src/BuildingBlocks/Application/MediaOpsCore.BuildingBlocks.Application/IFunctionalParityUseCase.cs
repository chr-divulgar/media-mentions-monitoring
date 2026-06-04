namespace MediaOpsCore.BuildingBlocks.Application;

public interface IFunctionalParityUseCase
{
    Task<FunctionalParityReport> ExecuteAsync(CancellationToken cancellationToken = default);
}