using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using System.Collections.Concurrent;
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
        FakeAlertService? alertService = null,
        ISourceIncidentRecorder? incidents = null,
        FakeStreamValidator? validator = null)
    {
        var options = new OperationsWorkerOptions();
        var captureSourceProvider = new StaticCaptureSourceProvider(options, repository);

        return new SourceAvailabilityReconciliationService(
            captureSourceProvider,
            validator ?? new FakeStreamValidator(),
            new FakePluginResolver(),
            new ServiceCollection().BuildServiceProvider(), // AudioCapturePlugin resolution fails, swallowed by TriggerCaptureAsync's own try/catch
            resolver,
            alertService ?? new FakeAlertService(alertActive: false),
            NullLogger<SourceAvailabilityReconciliationService>.Instance,
            incidents);
    }

    private sealed class TimingOutConnectivity : IConnectivityMonitor
    {
        public int Reads;

        public bool IsOnline
        {
            get
            {
                Interlocked.Increment(ref Reads);
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }
        }

        public Task WaitUntilOnlineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event Action? CameOnline
        {
            add { }
            remove { }
        }
    }

    [Fact]
    public async Task The_service_keeps_running_when_a_check_throws_a_timeout()
    {
        // A TaskCanceledException that is not a shutdown must not leave the loop: it would stop the host and every capture.
        var connectivity = new TimingOutConnectivity();
        var options = new OperationsWorkerOptions();
        var sut = new SourceAvailabilityReconciliationService(
            new StaticCaptureSourceProvider(options, new FakeCaptureSourceRepository([])),
            new FakeStreamValidator(), new FakePluginResolver(), new ServiceCollection().BuildServiceProvider(),
            new FakeLiveStreamUrlResolver(succeeds: true), new FakeAlertService(alertActive: false),
            NullLogger<SourceAvailabilityReconciliationService>.Instance, null, connectivity);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.True(connectivity.Reads >= 1);
        Assert.False(sut.ExecuteTask!.IsCompleted);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_records_why_a_youtube_source_could_not_be_recovered()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = CreateSut(
            new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]),
            new FakeLiveStreamUrlResolver(succeeds: false, detail: "ERROR: The channel is not currently live"),
            incidents: recorder);

        await sut.TriggerImmediateReconciliationAsync();

        var incident = Assert.Single(recorder.Records);
        Assert.Equal(("caracol-tv", IncidentKinds.RetryFailed), (incident.SourceId, incident.Kind));
        Assert.Contains("not currently live", incident.Reason);
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_records_the_recovery()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = CreateSut(
            new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]),
            new FakeLiveStreamUrlResolver(succeeds: true),
            incidents: recorder);

        await sut.TriggerImmediateReconciliationAsync();

        Assert.Equal(IncidentKinds.Recovered, Assert.Single(recorder.Records).Kind);
    }

    [Fact]
    public async Task TriggerImmediateReconciliationAsync_explains_a_paused_recovery_when_cookies_need_renewal()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = CreateSut(
            new FakeCaptureSourceRepository([YouTubeSource("caracol-tv")]),
            new FakeLiveStreamUrlResolver(succeeds: true),
            alertService: new FakeAlertService(alertActive: true),
            incidents: recorder);

        await sut.TriggerImmediateReconciliationAsync();

        var incident = Assert.Single(recorder.Records);
        Assert.Equal(IncidentKinds.RetryFailed, incident.Kind);
        Assert.Contains("cookies", incident.Reason);
    }

    [Fact]
    public async Task ReportAsync_records_every_failure_with_its_reason_even_while_a_recovery_is_running()
    {
        var recorder = new FakeIncidentRecorder();
        var source = RadioSource("blu-radio", isExcluded: false);
        var sut = CreateSut(
            new FakeCaptureSourceRepository([source]),
            new FakeLiveStreamUrlResolver(succeeds: false),
            incidents: recorder);

        await sut.ReportAsync(source, new AudioCaptureExecutionResult(false, "", "System.InvalidOperationException: av_interleaved_write_frame failed with FFmpeg error code -28.\n   at X()"));
        await sut.ReportAsync(source, new AudioCaptureExecutionResult(false, "", "Stream ended (EOF)."));

        var failures = recorder.Records.Where(r => r.Kind == IncidentKinds.Failed).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.Contains("-28", failures[0].Reason);
        Assert.Contains("EOF", failures[1].Reason);
    }

    [Fact]
    public async Task ReportAsync_records_an_exclusion_when_the_stream_content_is_unusable()
    {
        var recorder = new FakeIncidentRecorder();
        var source = RadioSource("blu-radio", isExcluded: false);
        var sut = CreateSut(
            new FakeCaptureSourceRepository([source]),
            new FakeLiveStreamUrlResolver(succeeds: false),
            incidents: recorder);

        await sut.ReportAsync(source, new AudioCaptureExecutionResult(false, "", "repeating", excludeSource: true));

        var incident = Assert.Single(recorder.Records);
        Assert.Equal(IncidentKinds.Excluded, incident.Kind);
    }

    [Fact]
    public async Task Hot_recovery_records_each_failed_retry_with_the_validator_error()
    {
        var recorder = new FakeIncidentRecorder();
        var source = RadioSource("blu-radio", isExcluded: false);
        var sut = CreateSut(
            new FakeCaptureSourceRepository([source]),
            new FakeLiveStreamUrlResolver(succeeds: false),
            incidents: recorder,
            validator: new FakeStreamValidator("avformat_open_input failed with FFmpeg error code -138"));

        await sut.ReportAsync(source, new AudioCaptureExecutionResult(false, "", "Stream ended (EOF)."));
        var retry = await recorder.WaitForAsync(IncidentKinds.RetryFailed);

        Assert.Equal("blu-radio", retry.SourceId);
        Assert.Contains("-138", retry.Reason);
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

    private sealed class FakeStreamValidator(string? error = null) : IStartupStreamValidator
    {
        public Task<StartupStreamValidationResult> ValidateAsync(string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StartupStreamValidationResult(Succeeded: false, ErrorMessage: error));
    }

    private sealed class FakeIncidentRecorder : ISourceIncidentRecorder
    {
        private readonly ConcurrentQueue<(string SourceId, string Kind, string? Reason)> records = new();

        public IReadOnlyList<(string SourceId, string Kind, string? Reason)> Records => records.ToArray();

        public void Record(string sourceId, string kind, string? reason = null) => records.Enqueue((sourceId, kind, reason));

        // Hot recovery runs on a background task, so its records land a moment after ReportAsync returns.
        public async Task<(string SourceId, string Kind, string? Reason)> WaitForAsync(string kind)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (records.FirstOrDefault(r => r.Kind == kind) is { Kind: not null } match)
                {
                    return match;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No '{kind}' incident was recorded.");
        }
    }

    private sealed class FakePluginResolver : IIngestionPluginResolver
    {
        public Task<PluginExecutionPlan> ResolveAsync(CaptureSource source, IngestionMode ingestionMode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed: TriggerCaptureAsync runs fire-and-forget and swallows exceptions.");
    }

    private sealed class FakeLiveStreamUrlResolver(bool succeeds, string? detail = null) : ILiveStreamUrlResolver
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
                : new LiveStreamResolutionResult(null, LiveStreamResolutionFailure.Unavailable, detail));
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
