namespace MediaOpsCore.Modules.Capture.Application;

public interface IPluginProfileProvider
{
    Task<IReadOnlyList<PluginProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);
}