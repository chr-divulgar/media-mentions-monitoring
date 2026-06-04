namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorkerOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
}