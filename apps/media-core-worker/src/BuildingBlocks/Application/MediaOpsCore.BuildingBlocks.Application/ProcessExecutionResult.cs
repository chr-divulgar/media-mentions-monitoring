namespace MediaOpsCore.BuildingBlocks.Application;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}