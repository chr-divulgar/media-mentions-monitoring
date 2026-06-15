using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StaticCaptureSourceProviderCanaryTests
{
    [Fact]
    public async Task ListActiveSourcesAsync_should_filter_to_continuous_media_allow_list()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new { sourceId = "s1", platform = "caracol", media = "radio", streamUrl = "https://example.com/r1" },
                new { sourceId = "s2", platform = "canal-1", media = "video", streamUrl = "https://example.com/v1" },
                new { sourceId = "s3", platform = "portal-a", media = "internet", streamUrl = "https://example.com/w1" }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var opts = new OperationsWorkerOptions
            {
                EnableCanaryMode = false,
                ContinuousMediaAllowList = "radio,video",
                CaptureSourcesFilePath = tempFilePath
            };
            var provider = new StaticCaptureSourceProvider(opts, new JsonFileCaptureSourceRepository(opts));

            var sources = await provider.ListActiveSourcesAsync();

            Assert.Equal(2, sources.Count);
            Assert.All(sources, source => Assert.Contains(source.Media, new[] { "radio", "video" }));
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
    public async Task ListActiveSourcesAsync_should_select_a_platform_subset_in_canary_mode()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new { sourceId = "s1", platform = "caracol", media = "radio", streamUrl = "https://example.com/c1" },
                new { sourceId = "s2", platform = "rcn", media = "radio", streamUrl = "https://example.com/r1" },
                new { sourceId = "s3", platform = "blu", media = "radio", streamUrl = "https://example.com/b1" },
                new { sourceId = "s4", platform = "lafm", media = "video", streamUrl = "https://example.com/l1" },
                new { sourceId = "s5", platform = "wradio", media = "video", streamUrl = "https://example.com/w1" },
                new { sourceId = "s6", platform = "portal-a", media = "internet", streamUrl = "https://example.com/p1" }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var opts = new OperationsWorkerOptions
            {
                EnableCanaryMode = true,
                CanaryPlatformMinPercent = 10,
                CanaryPlatformMaxPercent = 20,
                CanaryPlatformPercent = 20,
                ContinuousMediaAllowList = "radio,video",
                CaptureSourcesFilePath = tempFilePath
            };
            var provider = new StaticCaptureSourceProvider(opts, new JsonFileCaptureSourceRepository(opts));

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
                new { sourceId = "s1", platform = "caracol", media = "radio", streamUrl = "https://example.com/c1" },
                new { sourceId = "s2", platform = "rcn", media = "radio", streamUrl = "https://example.com/r1" },
                new { sourceId = "s3", platform = "portal-a", media = "internet", streamUrl = "https://example.com/p1" }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var opts = new OperationsWorkerOptions
            {
                EnableCanaryMode = true,
                CanaryPlatformMinPercent = 10,
                CanaryPlatformMaxPercent = 20,
                CanaryPlatformPercent = 20,
                CanaryPlatformAllowList = "rcn",
                ContinuousMediaAllowList = "radio,video",
                CaptureSourcesFilePath = tempFilePath
            };
            var provider = new StaticCaptureSourceProvider(opts, new JsonFileCaptureSourceRepository(opts));

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

    [Fact]
    public async Task ListConfiguredSourcesAsync_should_load_optional_primary_url()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "s1",
                    platform = "caracol",
                    media = "radio",
                    streamUrl = "https://example.com/c1",
                    primaryUrl = "https://caracol.example/en-vivo"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var opts = new OperationsWorkerOptions
            {
                EnableCanaryMode = false,
                ContinuousMediaAllowList = "radio",
                CaptureSourcesFilePath = tempFilePath
            };
            var provider = new StaticCaptureSourceProvider(opts, new JsonFileCaptureSourceRepository(opts));

            var sources = await provider.ListConfiguredSourcesAsync();

            Assert.Single(sources);
            Assert.Equal("https://caracol.example/en-vivo", sources[0].PrimaryUrl);
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
    public async Task ListConfiguredSourcesAsync_should_default_country_to_colombia_when_missing()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "s1",
                    platform = "caracol",
                    media = "radio",
                    streamUrl = "https://example.com/c1"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var opts = new OperationsWorkerOptions
            {
                EnableCanaryMode = false,
                CaptureSourcesFilePath = tempFilePath
            };
            var provider = new StaticCaptureSourceProvider(opts, new JsonFileCaptureSourceRepository(opts));

            var sources = await provider.ListConfiguredSourcesAsync();

            Assert.Single(sources);
            Assert.Equal("colombia", sources[0].Country);
            Assert.Equal(-300, sources[0].UtcOffsetMinutes);
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