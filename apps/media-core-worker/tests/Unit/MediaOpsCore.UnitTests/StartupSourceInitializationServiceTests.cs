using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
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

            var provider = new StaticCaptureSourceProvider(options, new JsonFileCaptureSourceRepository(options));
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
                new NoOpLiveStreamUrlResolver(),
                new NoOpCookiesAlertService(),
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();
            var effective = await provider.ListActiveSourcesAsync();

            Assert.Single(effective);
            Assert.Equal("healthy", effective[0].SourceId);
            // 1 call for the failed source (recovery path) + 1 fire-and-forget call for healthy (fallback population)
            Assert.True(discovery.Calls >= 1);
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
    public async Task InitializeAsync_should_exclude_a_failed_source_even_when_discovery_would_have_found_a_working_url()
    {
        // DiscoverAndPersistFallbacksAsync only ever runs for sources whose primary streamUrl
        // already validated (see StartupSourceInitializationService.cs) and is fire-and-forget:
        // it persists fallbackStreamUrls for a *future* run, it never promotes a currently-failed
        // source back into this run's resolved set. A source with no existing fallbacks and a
        // failing streamUrl has no conservative candidate to try, so it stays excluded this run —
        // even though FakeDiscovery below "would" have found a working URL.
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

            var provider = new StaticCaptureSourceProvider(options, new JsonFileCaptureSourceRepository(options));
            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://ok.example.com/live.aac"] = true,
                ["https://bad.example.com/live.aac"] = false,
                ["https://resolved.example.com/live.m3u8"] = true
            });
            var discovery = new FakeDiscovery(["https://resolved.example.com/live.m3u8"]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                new NoOpLiveStreamUrlResolver(),
                new NoOpCookiesAlertService(),
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();
            var effective = await provider.ListActiveSourcesAsync();

            Assert.Single(effective);
            Assert.Equal("healthy", effective[0].SourceId);
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://ok.example.com/live.aac", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validator.Calls, url => string.Equals(url, "https://bad.example.com/live.aac", StringComparison.OrdinalIgnoreCase));

            var configuredAfterStartup = await provider.ListConfiguredSourcesAsync();
            var failedSource = configuredAfterStartup.Single(source => source.SourceId == "failed");
            Assert.True(failedSource.IsExcluded);
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
    public async Task InitializeAsync_should_persist_a_discovered_url_as_fallback_even_when_it_looks_tokenized()
    {
        // DiscoverAndPersistFallbacksAsync fires for any source with a primaryUrl regardless of
        // whether its own streamUrl just failed (see the unconditional `_ = ...` call before the
        // validation-succeeded branch in StartupSourceInitializationService.cs) — but it is fire-
        // and-forget, so the test must wait for fallbackStreamUrls to actually land on disk rather
        // than asserting immediately after InitializeAsync returns.
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

            var provider = new StaticCaptureSourceProvider(options, new JsonFileCaptureSourceRepository(options));
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
                new NoOpLiveStreamUrlResolver(),
                new NoOpCookiesAlertService(),
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();

            Assert.Empty(await provider.ListActiveSourcesAsync());

            var persisted = await WaitForFallbacksAsync(provider, "failed", expectedCount: 1);
            Assert.Contains(discoveredTokenizedUrl, persisted.FallbackStreamUrls);
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

            var provider = new StaticCaptureSourceProvider(options, new JsonFileCaptureSourceRepository(options));
            var validator = new FakeValidator(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://bad.example.com/live.aac"] = false,
                ["https://fallback1.example.com/live.aac"] = true,
                ["https://fallback2.example.com/stream"] = true,
                ["https://invalid.example.com/live.aac"] = false
            });
            var discovery = new FakeDiscovery(
            [
                "https://fallback1.example.com/live.aac",
                "https://invalid.example.com/live.aac",
                "https://fallback2.example.com/stream"
            ]);

            var sut = new StartupSourceInitializationService(
                options,
                provider,
                validator,
                discovery,
                new NoOpLiveStreamUrlResolver(),
                new NoOpCookiesAlertService(),
                NullLogger<StartupSourceInitializationService>.Instance);

            await sut.InitializeAsync();

            Assert.Empty(await provider.ListActiveSourcesAsync());

            var persisted = await WaitForFallbacksAsync(provider, "failed", expectedCount: 2);
            Assert.Contains("https://fallback1.example.com/live.aac", persisted.FallbackStreamUrls);
            Assert.Contains("https://fallback2.example.com/stream", persisted.FallbackStreamUrls);
            Assert.DoesNotContain("https://invalid.example.com/live.aac", persisted.FallbackStreamUrls);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    // DiscoverAndPersistFallbacksAsync is fire-and-forget by design (it must never delay startup
    // capture) — poll instead of asserting immediately after InitializeAsync returns.
    private static async Task<CaptureSource> WaitForFallbacksAsync(
        StaticCaptureSourceProvider provider, string sourceId, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var configured = await provider.ListConfiguredSourcesAsync();
            var source = configured.SingleOrDefault(s => s.SourceId == sourceId);
            if (source is not null && source.FallbackStreamUrls.Count >= expectedCount)
            {
                return source;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"'{sourceId}' never reached {expectedCount} persisted fallback URL(s).");
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

    private sealed class NoOpLiveStreamUrlResolver : ILiveStreamUrlResolver
    {
        public bool CanResolve(CaptureSource source) => false;

        public Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(
            CaptureSource source, CancellationToken cancellationToken = default)
            => Task.FromResult(new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.Unavailable));
    }

    private sealed class NoOpCookiesAlertService : IYouTubeCookiesAlertService
    {
        public string AlertFilePath => Path.Combine(Path.GetTempPath(), "noop-alert.flag");
        public bool AlertExists() => false;
        public void WriteAlert(string sourceId, string errorMessage) { }
        public void ClearAlert() { }
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