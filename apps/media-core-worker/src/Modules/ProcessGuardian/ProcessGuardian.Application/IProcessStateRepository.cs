using MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;

namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public interface IProcessStateRepository
{
    Task<IReadOnlyList<ProcessState>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(ProcessState state, CancellationToken cancellationToken = default);
}