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
        Assert.Equal(64, options.CaptureMaxDegreeOfParallelism);
        Assert.True(options.EnableStartupValidation);
        Assert.True(options.EnableStartupDiscoveryOnFailedOnly);
        Assert.Equal(12, options.StartupValidationTimeoutSeconds);
        Assert.Equal(10, options.StartupDiscoveryRequestTimeoutSeconds);
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
              "captureMaxDegreeOfParallelism": 12,
              "enableCanaryMode": true,
              "canaryPlatformPercent": 40,
              "canaryPlatformMinPercent": 20,
              "canaryPlatformMaxPercent": 90,
              "canaryPlatformAllowList": "javeriana,colmundo",
                            "stageFilesystemRootPath": "stage-evidence-custom",
                            "defaultWavWindowDurationSeconds": 15,
                            "defaultOpusFlushIntervalSeconds": 20,
                            "defaultOpusRotationIntervalHours": 2,
                            "defaultOpusBitrateKbps": 96,
                            "enableDecoderReconnect": true,
                            "decoderReconnectDelayMaxSeconds": 8,
                            "rtspPreferTcp": true,
                            "enableWavSilenceChunking": true,
                            "wavSilenceMinChunkSeconds": 21,
                            "wavSilenceMaxChunkSeconds": 31,
                            "wavSilenceHoldMilliseconds": 350,
                            "wavSilenceAnalysisWindowMilliseconds": 25,
                            "wavSilenceThresholdDb": -38.5,
                            "enableStartupValidation": true,
                            "enableStartupDiscoveryOnFailedOnly": true,
                            "startupValidationTimeoutSeconds": 18,
                            "startupDiscoveryRequestTimeoutSeconds": 14
            }
            """);

            var options = OperationsWorkerOptionsLoader.Load(tempPath);

            Assert.Equal(TimeSpan.FromSeconds(12), options.HeartbeatInterval);
            Assert.Equal(TimeSpan.FromSeconds(45), options.DiscreteWorkerInterval);
            Assert.Equal("radio", options.ContinuousMediaAllowList);
            Assert.Equal(12, options.CaptureMaxDegreeOfParallelism);
            Assert.True(options.EnableCanaryMode);
            Assert.Equal(40, options.CanaryPlatformPercent);
            Assert.Equal(20, options.CanaryPlatformMinPercent);
            Assert.Equal(90, options.CanaryPlatformMaxPercent);
            Assert.Equal("javeriana,colmundo", options.CanaryPlatformAllowList);
            Assert.Equal("stage-evidence-custom", options.StageFilesystemRootPath);
            Assert.Equal(15, options.DefaultWavWindowDurationSeconds);
            Assert.Equal(20, options.DefaultOpusFlushIntervalSeconds);
            Assert.Equal(2, options.DefaultOpusRotationIntervalHours);
            Assert.Equal(96, options.DefaultOpusBitrateKbps);
            Assert.True(options.EnableDecoderReconnect);
            Assert.Equal(8, options.DecoderReconnectDelayMaxSeconds);
            Assert.True(options.RtspPreferTcp);
            Assert.True(options.EnableWavSilenceChunking);
            Assert.Equal(21, options.WavSilenceMinChunkSeconds);
            Assert.Equal(31, options.WavSilenceMaxChunkSeconds);
            Assert.Equal(350, options.WavSilenceHoldMilliseconds);
            Assert.Equal(25, options.WavSilenceAnalysisWindowMilliseconds);
            Assert.Equal(-38.5, options.WavSilenceThresholdDb);
            Assert.True(options.EnableStartupValidation);
            Assert.True(options.EnableStartupDiscoveryOnFailedOnly);
            Assert.Equal(18, options.StartupValidationTimeoutSeconds);
            Assert.Equal(14, options.StartupDiscoveryRequestTimeoutSeconds);
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

