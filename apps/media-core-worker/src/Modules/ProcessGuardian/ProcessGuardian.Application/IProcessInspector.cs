namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public interface IProcessInspector
{
    bool IsRunning(int processId);

    bool TryStop(int processId);
}