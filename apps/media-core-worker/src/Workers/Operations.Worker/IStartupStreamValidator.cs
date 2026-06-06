namespace MediaOpsCore.Workers.Operations;

public interface IStartupStreamValidator
{
    Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default);
}
