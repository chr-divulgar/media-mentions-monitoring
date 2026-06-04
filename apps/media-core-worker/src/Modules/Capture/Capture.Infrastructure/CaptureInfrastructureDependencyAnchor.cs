namespace MediaOpsCore.Modules.Capture.Infrastructure;

internal static class CaptureInfrastructureDependencyAnchor
{
    private static readonly Type[] Dependencies =
    {
        typeof(MediaOpsCore.Modules.Capture.Application.CaptureApplicationAssemblyMarker),
        typeof(MediaOpsCore.Modules.Capture.Domain.CaptureDomainAssemblyMarker)
    };
}