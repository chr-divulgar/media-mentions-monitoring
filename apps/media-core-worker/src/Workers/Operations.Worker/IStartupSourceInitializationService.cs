namespace MediaOpsCore.Workers.Operations;

public interface IStartupSourceInitializationService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
