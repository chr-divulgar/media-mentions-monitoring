using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StaticCaptureSourceProviderCanaryTests
{
    [Fact]
    public async Task ListActiveSourcesAsync_should_select_a_platform_subset_in_canary_mode()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new { sourceId = "s1", tenantId = "tenant-a", platform = "caracol", media = "radio", streamUrl = "https://example.com/c1" },
                new { sourceId = "s2", tenantId = "tenant-a", platform = "rcn", media = "radio", streamUrl = "https://example.com/r1" },
                new { sourceId = "s3", tenantId = "tenant-a", platform = "blu", media = "radio", streamUrl = "https://example.com/b1" },
                new { sourceId = "s4", tenantId = "tenant-a", platform = "lafm", media = "radio", streamUrl = "https://example.com/l1" },
                new { sourceId = "s5", tenantId = "tenant-a", platform = "wradio", media = "radio", streamUrl = "https://example.com/w1" }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions
            {
                EnableCanaryMode = true,
                CanaryPlatformMinPercent = 10,
                CanaryPlatformMaxPercent = 20,
                CanaryPlatformPercent = 20,
                CaptureSourcesFilePath = tempFilePath
            });

            var sources = await provider.ListActiveSourcesAsync();

            Assert.Single(sources);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    [Fact]
    public async Task ListActiveSourcesAsync_should_honor_canary_platform_allow_list()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new { sourceId = "s1", tenantId = "tenant-a", platform = "caracol", media = "radio", streamUrl = "https://example.com/c1" },
                new { sourceId = "s2", tenantId = "tenant-a", platform = "rcn", media = "radio", streamUrl = "https://example.com/r1" }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions
            {
                EnableCanaryMode = true,
                CanaryPlatformMinPercent = 10,
                CanaryPlatformMaxPercent = 20,
                CanaryPlatformPercent = 20,
                CanaryPlatformAllowList = "rcn",
                CaptureSourcesFilePath = tempFilePath
            });

            var sources = await provider.ListActiveSourcesAsync();

            Assert.Single(sources);
            Assert.Equal("rcn", sources[0].Platform);
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