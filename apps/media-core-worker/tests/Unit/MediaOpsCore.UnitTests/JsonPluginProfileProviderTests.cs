using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class JsonPluginProfileProviderTests
{
    [Fact]
    public async Task ListProfilesAsync_should_load_profiles_from_json_file()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"plugin-profiles-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(new[]
        {
            new
            {
                pluginId = "radio-default",
                media = "radio",
                platform = (string?)null,
                ingestionMode = "continuous",
                wavWindowDurationSeconds = 15,
                opusFlushIntervalSeconds = 30,
                opusRotationIntervalHours = 1
            },
            new
            {
                pluginId = "web-discrete",
                media = "internet",
                platform = (string?)"site-a",
                ingestionMode = "discrete",
                wavWindowDurationSeconds = 20,
                opusFlushIntervalSeconds = 20,
                opusRotationIntervalHours = 2
            }
        }));

        try
        {
            var options = new OperationsWorkerOptions { PluginProfilesFilePath = tempFilePath };
            var provider = new JsonPluginProfileProvider(options);

            var profiles = await provider.ListProfilesAsync();

            Assert.Equal(2, profiles.Count);
            Assert.Contains(profiles, profile =>
                profile.PluginId == "radio-default" &&
                profile.IngestionMode == IngestionMode.Continuous &&
                profile.WavWindowDuration == TimeSpan.FromSeconds(15) &&
                profile.OpusFlushInterval == TimeSpan.FromSeconds(30));
            Assert.Contains(profiles, profile =>
                profile.PluginId == "web-discrete" &&
                profile.IngestionMode == IngestionMode.Discrete &&
                profile.Platform == "site-a" &&
                profile.WavWindowDuration == TimeSpan.FromSeconds(20) &&
                profile.OpusRotationInterval == TimeSpan.FromHours(2));
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }
}

