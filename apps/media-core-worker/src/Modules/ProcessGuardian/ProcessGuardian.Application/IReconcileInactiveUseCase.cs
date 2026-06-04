namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public interface IReconcileInactiveUseCase
{
    Task<ReconcileInactiveResult> ExecuteAsync(CancellationToken cancellationToken = default);
}