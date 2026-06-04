namespace MediaOpsCore.Modules.Segmentation.Infrastructure;

internal static class SegmentationInfrastructureDependencyAnchor
{
    private static readonly Type[] Dependencies =
    {
        typeof(MediaOpsCore.Modules.Segmentation.Application.SegmentationApplicationAssemblyMarker),
        typeof(MediaOpsCore.Modules.Segmentation.Domain.SegmentationDomainAssemblyMarker)
    };
}