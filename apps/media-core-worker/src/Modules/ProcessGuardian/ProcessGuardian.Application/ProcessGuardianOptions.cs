namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed class ProcessGuardianOptions
{
    public TimeSpan RestartTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RestartCommandTimeout { get; set; } = TimeSpan.FromSeconds(20);
}