using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YouTubeHealthSnapshotProviderTests
{
    private static CaptureSource YouTubeSource(string sourceId, bool isExcluded = false) =>
        new(sourceId, tenantId: "tenant-1", platform: "youtube", media: "television",
            streamUrl: "https://example.com/placeholder", isExcluded: isExcluded);

    private static CaptureSource RadioSource(string sourceId) =>
        new(sourceId, tenantId: "tenant-1", platform: "generic", media: "radio",
            streamUrl: "https://example.com/radio", isExcluded: false);

    private static CookiesValidationResult ValidResult() =>
        new(IsValid: true, FileExists: true, CookieCount: 5,
            EarliestExpiration: DateTime.UtcNow.AddDays(30), HasYouTubeDomain: true, Message: "Valid");

    [Fact]
    public async Task GetSnapshotAsync_reports_no_auth_alert_and_valid_cookies_when_healthy()
    {
        var repository = new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]);
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository);
        provider.SetResolvedSources([YouTubeSource("caracol-tv")]);

        var sut = new YouTubeHealthSnapshotProvider(
            new OperationsWorkerOptions { YoutubeCookiesFilePath = "cookies.txt" },
            new FakeAlertService(alertActive: false),
            new FakeCookiesValidator(ValidResult()),
            provider,
            new FakeLiveStreamUrlResolver());

        var snapshot = await sut.GetSnapshotAsync();

        Assert.False(snapshot.AuthAlertActive);
        Assert.True(snapshot.CookiesValidation.IsValid);
        Assert.Empty(snapshot.ExcludedTvSourceIds);
        Assert.Equal(1, snapshot.TotalTvSourceCount);
        Assert.Equal(1, snapshot.ActiveTvSourceCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_reports_auth_alert_active_when_flag_exists()
    {
        var repository = new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]);
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository);
        provider.SetResolvedSources([]);

        var sut = new YouTubeHealthSnapshotProvider(
            new OperationsWorkerOptions { YoutubeCookiesFilePath = "cookies.txt" },
            new FakeAlertService(alertActive: true),
            new FakeCookiesValidator(ValidResult() with { IsValid = false, Message = "Cookies expired" }),
            provider,
            new FakeLiveStreamUrlResolver());

        var snapshot = await sut.GetSnapshotAsync();

        Assert.True(snapshot.AuthAlertActive);
        Assert.False(snapshot.CookiesValidation.IsValid);
    }

    [Fact]
    public async Task GetSnapshotAsync_counts_excluded_and_active_tv_sources_separately()
    {
        var configured = new[]
        {
            YouTubeSource("caracol-tv"),
            YouTubeSource("rcn-tv"),
            RadioSource("radio-nacional"),
        };
        var repository = new FakeCaptureSourceRepository(configured);
        var provider = new StaticCaptureSourceProvider(new OperationsWorkerOptions(), repository);
        // Only caracol-tv resolved/active; rcn-tv is currently excluded; radio is not a TV source at all.
        provider.SetResolvedSources([YouTubeSource("caracol-tv"), RadioSource("radio-nacional")]);

        var sut = new YouTubeHealthSnapshotProvider(
            new OperationsWorkerOptions { YoutubeCookiesFilePath = "cookies.txt" },
            new FakeAlertService(alertActive: false),
            new FakeCookiesValidator(ValidResult()),
            provider,
            new FakeLiveStreamUrlResolver());

        var snapshot = await sut.GetSnapshotAsync();

        Assert.Equal(2, snapshot.TotalTvSourceCount);
        Assert.Equal(1, snapshot.ActiveTvSourceCount);
        Assert.Equal(["rcn-tv"], snapshot.ExcludedTvSourceIds);
    }

    private sealed class FakeAlertService(bool alertActive) : IYouTubeCookiesAlertService
    {
        public string AlertFilePath => "alert.flag";
        public bool AlertExists() => alertActive;
        public void WriteAlert(string sourceId, string errorMessage) { }
        public void ClearAlert() { }
    }

    private sealed class FakeCookiesValidator(CookiesValidationResult result) : IYouTubeCookiesValidator
    {
        public Task<CookiesValidationResult> ValidateCookiesAsync(string? cookiesFilePath, string? cookiesContent = null) =>
            Task.FromResult(result);

        public Task<bool> TestCookiesWithYtdlpAsync(string cookiesFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(result.IsValid);
    }

    private sealed class FakeLiveStreamUrlResolver : ILiveStreamUrlResolver
    {
        public bool CanResolve(CaptureSource source) =>
            source.Media.Equals("television", StringComparison.OrdinalIgnoreCase)
            && source.Platform.Equals("youtube", StringComparison.OrdinalIgnoreCase);

        public Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(
            CaptureSource source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed for these tests.");
    }

    private sealed class FakeCaptureSourceRepository(IReadOnlyList<CaptureSource> sources) : ICaptureSourceRepository
    {
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
