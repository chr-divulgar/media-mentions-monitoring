namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public interface IProcessMonitorUseCase
{
    Task<ProcessMonitorResult> ExecuteAsync(CancellationToken cancellationToken = default);
}