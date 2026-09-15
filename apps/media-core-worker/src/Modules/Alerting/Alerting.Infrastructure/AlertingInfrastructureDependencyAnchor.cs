namespace MediaOpsCore.Modules.Alerting.Infrastructure;

internal static class AlertingInfrastructureDependencyAnchor
{
    private static readonly Type[] Dependencies =
    {
        typeof(MediaOpsCore.Modules.Alerting.Application.AlertingApplicationAssemblyMarker),
        typeof(MediaOpsCore.Modules.Alerting.Domain.AlertingDomainAssemblyMarker)
    };
}
