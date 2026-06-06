using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public interface IAudioCapturePlugin
{
    Task<AudioCaptureExecutionResult> CaptureAsync(
        CaptureSource source,
        PluginExecutionPlan plan,
        CancellationToken cancellationToken = default);
}
