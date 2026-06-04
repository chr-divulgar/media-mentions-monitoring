namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed record ProcessMonitorResult(int Inspected, int Restarted, int TimedOut);