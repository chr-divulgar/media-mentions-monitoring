using System.Text.Json;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StartupSourceInitializationServiceTests
{
    [Fact]
    public async Task InitializeAsync_should_exclude_sources_that_fail_validation_and_are_not_recovered()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "healthy",
                    platform = "a",
                    media = "radio",
                    streamUrl = "https://ok.example.com/live.aac",
                    primaryUrl = "https://site.example.com/healthy"
                },
                new
                {
                    sourceId = "failed",
                    platform = "b",
                    media = "radio",
                    streamUrl = "https://bad.example.com/live.aac",
                    primaryUrl = "https://site.example.com/failed"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var options = new OperationsWorkerOptions
            {
                CaptureSourcesFilePath = tempFilePath,
                EnableCanaryMode = false,
                EnableStartupValidation = true,
                EnableStartupDiscoveryOnFailedOnly = true
            };

            var provider = new StaticCaptureSourceProvider(options);
            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://ok.example.com/live.aac"] = true,
                ["https://bad.example.com/live.aac"] = false
            });
            var discovery = new FakeDiscovery([]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();
            var effective = await provider.ListActiveSourcesAsync();

            Assert.Single(effective);
            Assert.Equal("healthy", effective[0].SourceId);
            Assert.Equal(1, discovery.Calls);
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
    public async Task InitializeAsync_should_discover_only_failed_sources_and_replace_stream_url_when_revalidated()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "healthy",
                    platform = "a",
                    media = "radio",
                    streamUrl = "https://ok.example.com/live.aac",
                    primaryUrl = "https://site.example.com/healthy"
                },
                new
                {
                    sourceId = "failed",
                    platform = "b",
                    media = "radio",
                    streamUrl = "https://bad.example.com/live.aac",
                    primaryUrl = "https://site.example.com/failed"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var options = new OperationsWorkerOptions
            {
                CaptureSourcesFilePath = tempFilePath,
                EnableCanaryMode = false,
                EnableStartupValidation = true,
                EnableStartupDiscoveryOnFailedOnly = true
            };

            var provider = new StaticCaptureSourceProvider(options);
            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://ok.example.com/live.aac"] = true,
                ["https://bad.example.com/live.aac"] = false,
                ["https://resolved-invalid.example.com/live.m3u8"] = false,
                ["https://resolved.example.com/live.m3u8"] = true
            });
            var discovery = new FakeDiscovery(
            [
                "https://resolved-invalid.example.com/live.m3u8",
                "https://resolved.example.com/live.m3u8"
            ]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();
            var effective = await provider.ListActiveSourcesAsync();

            Assert.Equal(2, effective.Count);
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://ok.example.com/live.aac", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://bad.example.com/live.aac", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://resolved-invalid.example.com/live.m3u8", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://resolved.example.com/live.m3u8", StringComparison.OrdinalIgnoreCase));

            var recovered = effective.Single(source => source.SourceId == "failed");
            Assert.Equal("https://resolved.example.com/live.m3u8", recovered.StreamUrl);
            Assert.Equal(1, discovery.Calls);

            var configuredAfterStartup = await provider.ListConfiguredSourcesAsync();
            var persisted = configuredAfterStartup.Single(source => source.SourceId == "failed");
            Assert.Equal("https://resolved.example.com/live.m3u8", persisted.StreamUrl);
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
    public async Task InitializeAsync_should_persist_discovered_stream_url_even_when_it_looks_tokenized()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "failed",
                    platform = "b",
                    media = "radio",
                    streamUrl = "https://bad.example.com/live.aac",
                    primaryUrl = "https://site.example.com/failed"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var options = new OperationsWorkerOptions
            {
                CaptureSourcesFilePath = tempFilePath,
                EnableCanaryMode = false,
                EnableStartupValidation = true,
                EnableStartupDiscoveryOnFailedOnly = true
            };

            var provider = new StaticCaptureSourceProvider(options);
            var discoveredTokenizedUrl = "https://stream-177.zeno.fm/t8sz23cfhfhvv?zt=eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjE4OTM0NTYwMDB9.signature";

            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://bad.example.com/live.aac"] = false,
                [discoveredTokenizedUrl] = true
            });
            var discovery = new FakeDiscovery([discoveredTokenizedUrl]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();

            var active = await provider.ListActiveSourcesAsync();
            Assert.Single(active);
            Assert.Equal(discoveredTokenizedUrl, active[0].StreamUrl);

            var configuredAfterStartup = await provider.ListConfiguredSourcesAsync();
            Assert.Single(configuredAfterStartup);
            Assert.Equal(discoveredTokenizedUrl, configuredAfterStartup[0].StreamUrl);
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
    public async Task InitializeAsync_should_persist_all_valid_discovered_urls_as_fallbacks()
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"capture-sources-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "failed",
                    platform = "b",
                    media = "radio",
                    streamUrl = "https://bad.example.com/live.aac",
                    primaryUrl = "https://site.example.com/failed"
                }
            };

            await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(payload));

            var options = new OperationsWorkerOptions
            {
                CaptureSourcesFilePath = tempFilePath,
                EnableCanaryMode = false,
                EnableStartupValidation = true,
                EnableStartupDiscoveryOnFailedOnly = true
            };

            var provider = new StaticCaptureSourceProvider(options);
            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://bad.example.com/live.aac"] = false,
                ["https://primary.example.com/live.m3u8"] = true,
                ["https://fallback1.example.com/live.aac"] = true,
                ["https://fallback2.example.com/stream"] = true,
                ["https://invalid.example.com/live.aac"] = false
            });
            var discovery = new FakeDiscovery(
            [
                "https://primary.example.com/live.m3u8",
                "https://fallback1.example.com/live.aac",
                "https://invalid.example.com/live.aac",
                "https://fallback2.example.com/stream"
            ]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();

            var active = await provider.ListActiveSourcesAsync();
            Assert.Single(active);

            var recovered = active[0];
            Assert.Equal("https://primary.example.com/live.m3u8", recovered.StreamUrl);
            Assert.Equal(2, recovered.FallbackStreamUrls.Count);
            Assert.Contains("https://fallback1.example.com/live.aac", recovered.FallbackStreamUrls);
            Assert.Contains("https://fallback2.example.com/stream", recovered.FallbackStreamUrls);

            // Verify persisted to file
            var configured = await provider.ListConfiguredSourcesAsync();
            var persisted = configured.Single(s => s.SourceId == "failed");
            Assert.Equal("https://primary.example.com/live.m3u8", persisted.StreamUrl);
            Assert.Equal(2, persisted.FallbackStreamUrls.Count);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private sealed class FakeValidator : IStartupStreamValidator
    {
        private readonly Dictionary<string, bool> outcomes;

        public FakeValidator(Dictionary<string, bool> outcomes)
        {
            this.outcomes = outcomes;
        }

        public List<string> Calls { get; } = [];

        public Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default)
        {
            Calls.Add(streamUrl);
            var succeeded = outcomes.TryGetValue(streamUrl, out var value) && value;
            return Task.FromResult(new StartupStreamValidationResult(succeeded, succeeded ? null : "failed"));
        }
    }

    private sealed class FakeDiscovery : IStartupSourceDiscoveryService
    {
        private readonly IReadOnlyList<string> resolvedUrls;

        public FakeDiscovery(IReadOnlyList<string> resolvedUrls)
        {
            this.resolvedUrls = resolvedUrls;
        }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> DiscoverStreamUrlsAsync(CaptureSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(resolvedUrls);
        }

        public Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(resolvedUrls.FirstOrDefault());
        }
    }
}