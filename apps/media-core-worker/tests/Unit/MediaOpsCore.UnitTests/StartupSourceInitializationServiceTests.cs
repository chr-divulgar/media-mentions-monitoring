using System.Text.Json;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StartupSourceInitializationServiceTests
{
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
                ["https://resolved.example.com/live.m3u8"] = true
            });
            var discovery = new FakeDiscovery("https://resolved.example.com/live.m3u8");

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
        private readonly string resolvedUrl;

        public FakeDiscovery(string resolvedUrl)
        {
            this.resolvedUrl = resolvedUrl;
        }

        public int Calls { get; private set; }

        public Task<string?> TryResolveStreamUrlAsync(CaptureSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(resolvedUrl);
        }
    }
}