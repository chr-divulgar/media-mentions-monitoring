using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class SourceAvailabilityReconciliationServiceTests
{
    private static CaptureSource YouTubeSource(string sourceId, bool isExcluded = true) =>
        new(sourceId, tenantId: "tenant-1", platform: "youtube", media: "television",
            streamUrl: "https://example.com/placeholder", isExcluded: isExcluded);

    private static CaptureSource RadioSource(string sourceId, bool isExcluded = true) =>
        new(sourceId, tenantId: "tenant-1", platform: "generic", media: "radio",
            streamUrl: "https://example.com/radio", isExcluded: isExcluded);

    private static SourceAvailabilityReconciliationService CreateSut(
        FakeCaptureSourceRepository repository,
        FakeLiveStreamUrlResolver resolver,
        FakeAlertService? alertService = null)
    {
        var options = new OperationsWorkerOptions();
        var captureSourceProvider = new StaticCaptureSourceProvider(options, repository);

        return new SourceAvailabilityReconciliationService(
            captureSourceProvider,
            new FakeStreamValidator(),
            new FakePluginResolver(),
            new ServiceCollection().BuildServiceProvider(), // AudioCapturePlugin resolution fails, swallowed by TriggerCaptureAsync's own try/catch
            resolver,
            alertService ?? new FakeAlertService(alertActive: false),
            NullLogger<SourceAvailabilityReconciliationService>.Instance);
    }

    private static readonly IReadOnlySet<string> NoneMissing = new HashSet<string>();

    [Fact]
    public void FindSilentlyStoppedSources_waits_one_tick_before_acting_on_a_stopped_session()
    {
        // A recovery that just succeeded replaces the stopped session a moment later.
        var (toRecover, stillMissing) = SourceAvailabilityReconciliationService.FindSilentlyStoppedSources(
            ["caracol_tv"], ["caracol_tv"], [], NoneMissing);

        Assert.Empty(toRecover);
        Assert.Contains("caracol_tv", stillMissing);
    }

    [Fact]
    public void FindSilentlyStoppedSources_recovers_a_session_still_stopped_on_the_next_tick()
    {
        // The case that left caracol_tv down for hours: resolved, session stopped, nothing retrying it.
        var (toRecover, _) = SourceAvailabilityReconciliationService.FindSilentlyStoppedSources(
            ["caracol_tv", "blu-radio"], ["caracol_tv"], [], new HashSet<string> { "caracol_tv" });

        Assert.Equal(["caracol_tv"], toRecover);
    }

    [Fact]
    public void FindSilentlyStoppedSources_skips_running_sessions_and_sources_already_in_hot_recovery()
    {
        var (toRecover, stillMissing) = SourceAvailabilityReconciliationService.FindSilentlyStoppedSources(
            ["blu-radio", "rcn_tv"], ["rcn_tv"], ["rcn_tv"], new HashSet<string> { "blu-radio", "rcn_tv" });

        Assert.Empty(toRecover);
        Assert.Empty(stillMissing);
    }

    [Fact]
    public void FindSilentlyStoppedSources_ignores_sources_that_never_had_a_session()
    {
        // Startup validation takes over a minute; those sources belong to startup, not this net.
        var (toRecover, stillMissing) = SourceAvailabilityReconciliationService.FindSilentlyStoppedSources(
            ["caracol_tv", "blu-radio"], [], [], new HashSet<string> { "caracol_tv", "blu-radio" });

        Assert.Empty(toRecover);
        Assert.Empty(stillMissing);
    }

    [Fact]
    public void BuildRadioRecoveryCandidates_retries_the_current_stream_url_first()
    {
        // Barrancas' only fallback was its own URL: variants alone left zero candidates, so a
        // stream that merely dropped could never be recovered without restarting the service.
        var source = new CaptureSource(
            "emisora-comunitaria-barrancas", "tenant-1", "generic", "radio",
            "https://streaming.example.com/8744/stream",
            fallbackStreamUrls: ["https://streaming.example.com/8744/stream", "http://streaming.example.com/8744/stream"]);

        var candidates = SourceAvailabilityReconciliationService.BuildRadioRecoveryCandidates(source);

        Assert.Equal(
            ["https://streaming.example.com/8744/stream", "http://streaming.example.com/8744/stream"],
            candidates);
    }

    [Theory]
    [InlineData(13, 22, 0)]
    [InlineData(13, 0, 5)]
    public void ResolveHotRecoveryDeadline_is_minute_59_of_the_hour_the_failure_happened_in(int hour, int minute, int second)
    {
        var now = new DateTimeOffset(2026, 10, 1, hour, minute, second, TimeSpan.FromHours(-5));

        var deadline = SourceAvailabilityReconciliationService.ResolveHotRecoveryDeadline(now);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, hour, 59, 0, TimeSpan.FromHours(-5)), deadline);
    }

    [Fact]
    public void ResolveHotRecoveryDeadline_is_already_reached_for_a_failure_during_minute_59()
    {
        // Same as the old `Minute == 59` check for this case — one attempt, then exclude.
        var now = new DateTimeOffset(2026, 10, 1, 13, 59, 30, TimeSpan.FromHours(-5));

        Assert.True(now >= SourceAvailabilityReconciliationService.ResolveHotRecoveryDeadline(now));
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_recovers_excluded_youtube_source_that_now_resolves()
    {
        var repository = new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]);
        var resolver = new FakeLiveStreamUrlResolver(succeeds: true);
        var sut = CreateSut(repository, resolver);

        var recoveredCount = await sut.TriggerImmediateReconciliationAsync();

        Assert.Equal(1, recoveredCount);
        Assert.Equal(["caracol-tv"], repository.ExclusionUpdates.Keys);
        Assert.False(repository.ExclusionUpdates["caracol-tv"]);
        Assert.True(repository.StreamUrlUpdates.ContainsKey("caracol-tv"));
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_leaves_source_excluded_when_resolution_still_fails()
    {
        var repository = new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]);
        var resolver = new FakeLiveStreamUrlResolver(succeeds: false);
        var sut = CreateSut(repository, resolver);

        var recoveredCount = await sut.TriggerImmediateReconciliationAsync();

        Assert.Equal(0, recoveredCount);
        Assert.True(repository.ExclusionUpdates["caracol-tv"]);
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_skips_recovery_while_auth_alert_is_active()
    {
        var repository = new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]);
        var resolver = new FakeLiveStreamUrlResolver(succeeds: true);
        var sut = CreateSut(repository, resolver, alertService: new FakeAlertService(alertActive: true));

        var recoveredCount = await sut.TriggerImmediateReconciliationAsync();

        Assert.Equal(0, recoveredCount);
        Assert.False(resolver.WasCalled);
        Assert.Empty(repository.ExclusionUpdates);
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_ignores_non_youtube_sources_and_already_active_sources()
    {
        var repository = new FakeCaptureSourceRepository(
        [
            YouTubeSource("caracol-tv", isExcluded: false), // already active — not attempted
            RadioSource("radio-nacional", isExcluded: true), // not a TV/YouTube source — not this trigger's concern
        ]);
        var resolver = new FakeLiveStreamUrlResolver(succeeds: true);
        var sut = CreateSut(repository, resolver);

        var recoveredCount = await sut.TriggerImmediateReconciliationAsync();

        Assert.Equal(0, recoveredCount);
        Assert.False(resolver.WasCalled);
    }

    private sealed class FakeAlertService(bool alertActive) : IYouTubeCookiesAlertService
    {
        public string AlertFilePath => "alert.flag";
        public bool AlertExists() => alertActive;
        public void WriteAlert(string sourceId, string errorMessage) { }
        public void ClearAlert() { }
    }

    private sealed class FakeStreamValidator : IStartupStreamValidator
    {
        public Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StartupStreamValidationResult(Succeeded: false));
    }

    private sealed class FakePluginResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(CaptureSource source, IngestionMode ingestionMode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed: TriggerCaptureAsync runs fire-and-forget and swallows exceptions.");
    }

    private sealed class FakeLiveStreamUrlResolver(bool succeeds) : ILiveStreamUrlResolver
    {
        public bool WasCalled { get; private set; }

        public bool CanResolve(CaptureSource source) =>
            source.Media.Equals("television", StringComparison.OrdinalIgnoreCase)
            && source.Platform.Equals("youtube", StringComparison.OrdinalIgnoreCase);

        public Task<LiveStreamResolutionResult> TryResolveStreamUrlAsync(
            CaptureSource source, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(succeeds
                ? new LiveStreamResolutionResult("https://example.com/resolved.m3u8", null)
                : new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.Unavailable));
        }
    }

    private sealed class FakeCaptureSourceRepository(IReadOnlyList<CaptureSource> sources) : ICaptureSourceRepository
    {
        public Dictionary<string, bool> ExclusionUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StreamUrlUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
        {
            StreamUrlUpdates[sourceId] = streamUrl;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
        {
            ExclusionUpdates[sourceId] = excluded;
            return Task.FromResult(true);
        }
    }
}
