namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed record ChunkProcessMonitorResult(int OrphansDetected, int OrphansStopped);