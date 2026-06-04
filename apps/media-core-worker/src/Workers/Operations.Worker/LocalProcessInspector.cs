using System.Diagnostics;
using MediaOpsCore.Modules.ProcessGuardian.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class LocalProcessInspector : IProcessInspector
{
    public bool IsRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public bool TryStop(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}