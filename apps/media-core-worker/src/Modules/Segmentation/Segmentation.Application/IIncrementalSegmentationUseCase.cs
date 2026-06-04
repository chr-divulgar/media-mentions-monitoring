namespace MediaOpsCore.Modules.Segmentation.Application;

public interface IIncrementalSegmentationUseCase
{
    Task<IncrementalSegmentationResult> ExecuteAsync(CancellationToken cancellationToken = default);
}