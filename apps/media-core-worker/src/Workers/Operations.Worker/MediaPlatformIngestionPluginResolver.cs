using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;

namespace MediaOpsCore.Workers.Operations;

public sealed class MediaPlatformIngestionPluginResolver : IIngestionPluginResolver
{
    private readonly IPluginProfileProvider profileProvider;
    private readonly OperationsWorkerOptions options;

    public MediaPlatformIngestionPluginResolver(
        IPluginProfileProvider profileProvider,
        OperationsWorkerOptions options)
    {
        this.profileProvider = profileProvider;
        this.options = options;
    }

    public async Task<PluginExecutionPlan> ResolveAsync(
        CaptureSource source,
        IngestionMode ingestionMode,
        CancellationToken cancellationToken = default)
    {
        var profiles = await profileProvider.ListProfilesAsync(cancellationToken).ConfigureAwait(false);

        var scoped = profiles
            .Where(profile => profile.IngestionMode == ingestionMode)
            .Where(profile => string.Equals(profile.Media, source.Media, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var platformMatch = scoped.FirstOrDefault(profile =>
            !string.IsNullOrWhiteSpace(profile.Platform) &&
            string.Equals(profile.Platform, source.Platform, StringComparison.OrdinalIgnoreCase));

        if (platformMatch is not null)
        {
            return ToExecutionPlan(platformMatch);
        }

        var mediaDefault = scoped.FirstOrDefault(profile => string.IsNullOrWhiteSpace(profile.Platform));
        if (mediaDefault is not null)
        {
            return ToExecutionPlan(mediaDefault);
        }

        if (ingestionMode == IngestionMode.Continuous && options.EnableLegacyCaptureProfileFallback)
        {
            return new PluginExecutionPlan(
                pluginId: "legacy-capture-default",
                toolExecutable: options.CaptureToolExecutable,
                toolArgumentsTemplate: options.CaptureToolArgumentsTemplate,
                commandTimeout: options.CaptureCommandTimeout);
        }

        throw new InvalidOperationException(
            $"No plugin profile configured for media '{source.Media}', platform '{source.Platform}', mode '{ingestionMode}'.");
    }

    private static PluginExecutionPlan ToExecutionPlan(PluginProfile profile)
    {
        return new PluginExecutionPlan(
            profile.PluginId,
            profile.ToolExecutable,
            profile.ToolArgumentsTemplate,
            profile.CommandTimeout);
    }
}