namespace MediaOpsCore.Modules.Capture.Application;

public interface IContinuousCaptureUseCase
{
    Task<ContinuousCaptureResult> ExecuteAsync(CancellationToken cancellationToken = default);
}