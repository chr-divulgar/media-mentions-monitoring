namespace MediaOpsCore.Modules.ProcessGuardian.Infrastructure;

internal static class ProcessGuardianInfrastructureDependencyAnchor
{
    private static readonly Type[] Dependencies =
    {
        typeof(MediaOpsCore.Modules.ProcessGuardian.Application.ProcessGuardianApplicationAssemblyMarker),
        typeof(MediaOpsCore.Modules.ProcessGuardian.Domain.ProcessGuardianDomainAssemblyMarker)
    };
}