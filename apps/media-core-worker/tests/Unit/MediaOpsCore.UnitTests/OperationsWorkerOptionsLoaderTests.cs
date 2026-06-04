using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class OperationsWorkerOptionsLoaderTests
{
    [Fact]
    public void Load_should_return_defaults_when_file_does_not_exist()
    {
        var options = OperationsWorkerOptionsLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
        Assert.Equal("stage/capture-sources.json", options.CaptureSourcesFilePath);
        Assert.Equal("stage/plugin-profiles.json", options.PluginProfilesFilePath);
        Assert.Equal("radio,video", options.ContinuousMediaAllowList);
    }

    [Fact]
    public void Load_should_apply_values_from_json_file()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"worker-options-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempPath, """
            {
              "heartbeatIntervalSeconds": 12,
              "discreteWorkerIntervalSeconds": 45,
              "captureSourcesFilePath": "stage/capture-sources.json",
              "pluginProfilesFilePath": "stage/plugin-profiles.json",
              "continuousMediaAllowList": "radio",
              "enableCanaryMode": true,
              "canaryPlatformPercent": 40,
              "canaryPlatformMinPercent": 20,
              "canaryPlatformMaxPercent": 90,
              "canaryPlatformAllowList": "javeriana,colmundo",
              "stageFilesystemRootPath": "stage-evidence-custom"
            }
            """);

            var options = OperationsWorkerOptionsLoader.Load(tempPath);

            Assert.Equal(TimeSpan.FromSeconds(12), options.HeartbeatInterval);
            Assert.Equal(TimeSpan.FromSeconds(45), options.DiscreteWorkerInterval);
            Assert.Equal("radio", options.ContinuousMediaAllowList);
            Assert.True(options.EnableCanaryMode);
            Assert.Equal(40, options.CanaryPlatformPercent);
            Assert.Equal(20, options.CanaryPlatformMinPercent);
            Assert.Equal(90, options.CanaryPlatformMaxPercent);
            Assert.Equal("javeriana,colmundo", options.CanaryPlatformAllowList);
            Assert.Equal("stage-evidence-custom", options.StageFilesystemRootPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
