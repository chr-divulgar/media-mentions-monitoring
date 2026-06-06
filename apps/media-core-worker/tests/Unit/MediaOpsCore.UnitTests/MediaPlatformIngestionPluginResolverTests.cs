using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class MediaPlatformIngestionPluginResolverTests
{
    [Fact]
    public async Task ResolveAsync_should_use_platform_override_before_media_default()
    {
        var provider = new FakePluginProfileProvider(new[]
        {
            new PluginProfile(
                pluginId: "video-default",
                media: "video",
                platform: null,
                ingestionMode: IngestionMode.Continuous,
                wavWindowDuration: TimeSpan.FromSeconds(10),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1)),
            new PluginProfile(
                pluginId: "youtube-override",
                media: "video",
                platform: "youtube",
                ingestionMode: IngestionMode.Continuous,
                wavWindowDuration: TimeSpan.FromSeconds(10),
                opusFlushInterval: TimeSpan.FromSeconds(20),
                opusRotationInterval: TimeSpan.FromHours(2))
        });

        var resolver = new MediaPlatformIngestionPluginResolver(provider, new OperationsWorkerOptions());
        var source = new CaptureSource("src-1", "global-ingestion", "youtube", "video", "https://example.com/watch?v=abc");

        var plan = await resolver.ResolveAsync(source, IngestionMode.Continuous);

        Assert.Equal("youtube-override", plan.PluginId);
        Assert.Equal(TimeSpan.FromSeconds(20), plan.OpusFlushInterval);
        Assert.Equal(TimeSpan.FromHours(2), plan.OpusRotationInterval);
    }

    [Fact]
    public async Task ResolveAsync_should_use_media_default_when_platform_override_is_missing()
    {
        var provider = new FakePluginProfileProvider(new[]
        {
            new PluginProfile(
                pluginId: "radio-default",
                media: "radio",
                platform: null,
                ingestionMode: IngestionMode.Continuous,
                wavWindowDuration: TimeSpan.FromSeconds(10),
                opusFlushInterval: TimeSpan.FromSeconds(30),
                opusRotationInterval: TimeSpan.FromHours(1))
        });

        var resolver = new MediaPlatformIngestionPluginResolver(provider, new OperationsWorkerOptions());
        var source = new CaptureSource("src-2", "global-ingestion", "caracol", "radio", "https://stream.example.com/live");

        var plan = await resolver.ResolveAsync(source, IngestionMode.Continuous);

        Assert.Equal("radio-default", plan.PluginId);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.OpusFlushInterval);
        Assert.Equal(TimeSpan.FromHours(1), plan.OpusRotationInterval);
    }

    [Fact]
    public async Task ResolveAsync_should_throw_when_profile_is_missing()
    {
        var resolver = new MediaPlatformIngestionPluginResolver(
            new FakePluginProfileProvider(Array.Empty<PluginProfile>()),
            new OperationsWorkerOptions());

        var source = new CaptureSource("src-3", "global-ingestion", "unknown-platform", "unknown-media", "https://unknown.example.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(source, IngestionMode.Continuous));
    }

    private sealed class FakePluginProfileProvider : IPluginProfileProvider
    {
        private readonly IReadOnlyList<PluginProfile> profiles;

        public FakePluginProfileProvider(IReadOnlyList<PluginProfile> profiles)
        {
            this.profiles = profiles;
        }

        public Task<IReadOnlyList<PluginProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(profiles);
        }
    }
}

