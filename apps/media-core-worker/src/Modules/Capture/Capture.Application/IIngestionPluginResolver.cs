using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public interface IIngestionPluginResolver
{
    Task<PluginExecutionPlan> ResolveAsync(
        CaptureSource source,
        IngestionMode ingestionMode,
        CancellationToken cancellationToken = default);
}