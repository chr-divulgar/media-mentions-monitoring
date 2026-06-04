namespace MediaOpsCore.BuildingBlocks.Infrastructure;

internal static class InfrastructureDependencyAnchor
{
    private static readonly Type[] Dependencies =
    {
        typeof(MediaOpsCore.BuildingBlocks.Application.ApplicationAssemblyMarker),
        typeof(MediaOpsCore.BuildingBlocks.Domain.DomainAssemblyMarker)
    };
}