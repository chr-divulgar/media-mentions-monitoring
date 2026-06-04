namespace MediaOpsCore.BuildingBlocks.Application;

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default);
}