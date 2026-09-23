using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class CaptureStatusSnapshotProviderTests
{
    // A fixed, non-today date keeps the "closed hour" scenarios deterministic — they never touch
    // the live/in-progress-hour path, which is the only part of the provider that reads real time.
    private static readonly DateOnly ClosedDate = new(2026, 1, 1);

    [Fact]
    public async Task GetSnapshotAsync_should_use_the_persisted_hour_file_status_and_coverage()
    {
        var evidenceStore = new FakeEvidenceFileStore();
        evidenceStore.Seed(
            "monitoringArtifacts/capture-summary-2026-01-01_00.json",
            new CaptureSummarySnapshot(
                "2026-01-01_00",
                "2026-01-01T00:59:59Z",
                [new SourceCaptureSummary("radio-a", "radio-a_2026-01-01_00-00-00.opus", 3400, 200, 94.4, "gap-filled", "00:59:00 -05:00")]));

        var provider = CreateProvider(
            sources: [CreateSource("radio-a")],
            evidenceStore: evidenceStore);

        var response = await provider.GetSnapshotAsync(ClosedDate);

        var hour0 = Assert.Single(response.Sources.Single(s => s.SourceId == "radio-a").Hours, h => h.Hour == 0);
        Assert.Equal("gap-filled", hour0.Status);
        Assert.Equal(94.4, hour0.CoveragePercent);
        Assert.Null(hour0.Segments);
    }

    [Fact]
    public async Task GetSnapshotAsync_should_report_no_data_when_the_hour_file_is_missing()
    {
        var provider = CreateProvider(
            sources: [CreateSource("radio-a")],
            evidenceStore: new FakeEvidenceFileStore());

        var response = await provider.GetSnapshotAsync(ClosedDate);

        var hour0 = Assert.Single(response.Sources.Single(s => s.SourceId == "radio-a").Hours, h => h.Hour == 0);
        Assert.Equal("no-data", hour0.Status);
        Assert.Null(hour0.CoveragePercent);
    }

    [Fact]
    public async Task GetSnapshotAsync_should_report_no_data_when_the_source_is_absent_from_an_existing_hour_file()
    {
        var evidenceStore = new FakeEvidenceFileStore();
        evidenceStore.Seed(
            "monitoringArtifacts/capture-summary-2026-01-01_00.json",
            new CaptureSummarySnapshot("2026-01-01_00", "2026-01-01T00:59:59Z", [
                new SourceCaptureSummary("radio-other", "radio-other_2026-01-01_00-00-00.opus", 3600, 0, 100.0, "ok", "00:59:00 -05:00"),
            ]));

        var provider = CreateProvider(
            sources: [CreateSource("radio-a")],
            evidenceStore: evidenceStore);

        var response = await provider.GetSnapshotAsync(ClosedDate);

        var hour0 = Assert.Single(response.Sources.Single(s => s.SourceId == "radio-a").Hours, h => h.Hour == 0);
        Assert.Equal("no-data", hour0.Status);
    }

    [Fact]
    public async Task GetSnapshotAsync_should_map_persisted_checkpoints_straight_onto_the_hour()
    {
        // The session brackets its own snapshots, so what is persisted already spans the window —
        // offsets are positions in that hour's recording and need no reprojection here.
        var evidenceStore = new FakeEvidenceFileStore();
        evidenceStore.Seed(
            "monitoringArtifacts/capture-summary-2026-01-01_00.json",
            new CaptureSummarySnapshot("2026-01-01_00", "2026-01-01T00:59:59Z", [
                new SourceCaptureSummary(
                    "radio-a", "radio-a_2026-01-01_00-00-00.opus", 3600, 0, 100.0, "ok", "00:59:00 -05:00",
                    [new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(1800, 1800, 0), new WindowCheckpoint(3600, 3600, 0)]),
            ]));

        var provider = CreateProvider(sources: [CreateSource("radio-a")], evidenceStore: evidenceStore);

        var response = await provider.GetSnapshotAsync(ClosedDate);

        var hour0 = Assert.Single(response.Sources.Single(s => s.SourceId == "radio-a").Hours, h => h.Hour == 0);
        Assert.NotNull(hour0.Segments);
        Assert.Equal(3600, hour0.Segments[^1].EndSeconds);
        Assert.All(hour0.Segments, s => Assert.Equal("captured", s.State));
    }

    [Fact]
    public async Task GetSnapshotAsync_should_report_no_data_for_the_current_hour_when_no_session_is_active()
    {
        var nowBogota = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5));
        var today = DateOnly.FromDateTime(nowBogota.DateTime);

        var provider = CreateProvider(
            sources: [CreateSource("radio-a")],
            evidenceStore: new FakeEvidenceFileStore(),
            liveCaptureProgressReader: new FakeLiveCaptureProgressReader(checkpointsBySource: []));

        var response = await provider.GetSnapshotAsync(today);

        var currentHour = Assert.Single(
            response.Sources.Single(s => s.SourceId == "radio-a").Hours,
            h => h.Hour == nowBogota.Hour);
        Assert.Equal("no-data", currentHour.Status);
        Assert.Null(currentHour.Segments);
    }

    [Fact]
    public async Task GetSnapshotAsync_should_report_live_with_segments_when_a_session_is_active()
    {
        var nowBogota = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5));
        var today = DateOnly.FromDateTime(nowBogota.DateTime);

        var reader = new FakeLiveCaptureProgressReader(new Dictionary<string, IReadOnlyList<WindowCheckpoint>>
        {
            ["radio-a"] = [new WindowCheckpoint(0, 0, 0), new WindowCheckpoint(300, 300, 0)],
        });

        var provider = CreateProvider(
            sources: [CreateSource("radio-a")],
            evidenceStore: new FakeEvidenceFileStore(),
            liveCaptureProgressReader: reader);

        var response = await provider.GetSnapshotAsync(today);

        var currentHour = Assert.Single(
            response.Sources.Single(s => s.SourceId == "radio-a").Hours,
            h => h.Hour == nowBogota.Hour);
        Assert.Equal("live", currentHour.Status);
        Assert.NotNull(currentHour.Segments);
    }

    [Fact]
    public async Task GetSnapshotAsync_should_peek_in_memory_evidence_for_an_hour_not_yet_flushed_to_disk()
    {
        // Regression test: WriteSummaryFileAsync only flushes an hour to disk once the NEXT hour
        // also closes for that source — without peeking hourlySummaries directly, a just-closed
        // hour would read as "no-data" for up to an hour even though it's already complete.
        var repository = new StageMirrorMonitoringArtifactRepository(
            new InMemoryMonitoringArtifactRepository(),
            new FakeEvidenceFileStore(),
            Array.Empty<IMonitoringArtifactDatabaseRepository>());
        await repository.UpsertAsync(new MonitoringArtifact(
            id: "capture-radio-a-1",
            tenantId: "global-ingestion",
            source: "radio-a",
            kind: "capture",
            payloadJson: "{\"succeeded\":true,\"capturedSeconds\":3600,\"silenceFilledSeconds\":0}",
            capturedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero)));

        var provider = new CaptureStatusSnapshotProvider(
            new FakeCaptureSourceRepository([CreateSource("radio-a")]),
            new FakeEvidenceFileStore(), // deliberately empty — nothing has been flushed to disk
            repository,
            new FakeLiveCaptureProgressReader(checkpointsBySource: []));

        var response = await provider.GetSnapshotAsync(ClosedDate);

        var hour0 = Assert.Single(response.Sources.Single(s => s.SourceId == "radio-a").Hours, h => h.Hour == 0);
        Assert.Equal("ok", hour0.Status);
    }

    private static CaptureStatusSnapshotProvider CreateProvider(
        IReadOnlyList<CaptureSource> sources,
        IEvidenceFileStore evidenceStore,
        ILiveCaptureProgressReader? liveCaptureProgressReader = null)
    {
        var monitoringArtifactRepository = new StageMirrorMonitoringArtifactRepository(
            new InMemoryMonitoringArtifactRepository(),
            new FakeEvidenceFileStore(),
            Array.Empty<IMonitoringArtifactDatabaseRepository>());

        return new CaptureStatusSnapshotProvider(
            new FakeCaptureSourceRepository(sources),
            evidenceStore,
            monitoringArtifactRepository,
            liveCaptureProgressReader ?? new FakeLiveCaptureProgressReader(checkpointsBySource: []));
    }

    private static CaptureSource CreateSource(string sourceId) =>
        new(sourceId, "global-ingestion", "radio", "radio", $"https://example.com/{sourceId}.m3u8");

    private sealed class FakeCaptureSourceRepository(IReadOnlyList<CaptureSource> sources) : ICaptureSourceRepository
    {
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeEvidenceFileStore : IEvidenceFileStore
    {
        private readonly Dictionary<string, object> files = new(StringComparer.Ordinal);

        public void Seed<T>(string relativePath, T payload) where T : notnull => files[relativePath] = payload;

        public Task WriteJsonAsync<T>(string relativePath, T payload, CancellationToken cancellationToken = default)
        {
            if (payload is not null) files[relativePath] = payload;
            return Task.CompletedTask;
        }

        public Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(files.TryGetValue(relativePath, out var value) ? (T?)value : default);

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            files.Remove(relativePath);
            return Task.CompletedTask;
        }
    }

    // The provider only attributes live progress to the hour that progress says it belongs to,
    // so a fake standing in for "recording right now" has to report the current window.
    private static DateTimeOffset CurrentHourStart()
    {
        var nowBogota = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5));
        return new DateTimeOffset(
            nowBogota.Year, nowBogota.Month, nowBogota.Day, nowBogota.Hour, 0, 0, nowBogota.Offset);
    }

    private sealed class FakeLiveCaptureProgressReader(Dictionary<string, IReadOnlyList<WindowCheckpoint>> checkpointsBySource) : ILiveCaptureProgressReader
    {
        public IReadOnlyCollection<string> ActiveSourceIds => checkpointsBySource.Keys;

        public LiveCaptureProgress? TryGetLiveProgress(string sourceId) =>
            checkpointsBySource.TryGetValue(sourceId, out var checkpoints)
                ? new LiveCaptureProgress(CurrentHourStart(), checkpoints[^1].ElapsedSeconds, checkpoints)
                : null;
    }
}
