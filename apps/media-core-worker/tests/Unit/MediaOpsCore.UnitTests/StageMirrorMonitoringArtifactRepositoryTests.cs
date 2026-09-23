using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StageMirrorMonitoringArtifactRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_should_keep_local_evidence_when_no_db_sink_is_configured()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var artifact = CreateArtifact("artifact-no-db");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_delete_local_evidence_after_successful_db_persist()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var dbSink = new SuccessfulDatabaseRepository();
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                new[] { dbSink });

            var artifact = CreateArtifact("artifact-db-ok");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.False(File.Exists(filePath));
            Assert.Equal(1, dbSink.UpsertCalls);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_keep_local_evidence_when_db_persist_fails()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                new[] { new FailingDatabaseRepository() });

            var artifact = CreateArtifact("artifact-db-fail");

            await repository.UpsertAsync(artifact);

            var filePath = BuildEvidencePath(tempRoot, artifact.Id);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_not_write_local_evidence_for_segment_artifacts()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var segmentArtifact = CreateArtifact(
                id: "segment-capture-unit-test-1",
                source: "unit-test",
                kind: "segment",
                capturedAtUtc: DateTimeOffset.UtcNow);

            await repository.UpsertAsync(segmentArtifact);

            var filePath = BuildEvidencePath(tempRoot, segmentArtifact.Id);
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task UpsertAsync_should_write_capture_evidence_only_once_per_source_per_hour()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions
            {
                StageFilesystemRootPath = tempRoot
            };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var firstCapture = CreateArtifact(
                id: "capture-unit-test-20260605150000000",
                source: "unit-test",
                kind: "capture",
                capturedAtUtc: new DateTimeOffset(2026, 6, 5, 15, 5, 0, TimeSpan.Zero));

            var sameHourCapture = CreateArtifact(
                id: "capture-unit-test-20260605153000000",
                source: "unit-test",
                kind: "capture",
                capturedAtUtc: new DateTimeOffset(2026, 6, 5, 15, 30, 0, TimeSpan.Zero));

            var nextHourCapture = CreateArtifact(
                id: "capture-unit-test-20260605160000000",
                source: "unit-test",
                kind: "capture",
                capturedAtUtc: new DateTimeOffset(2026, 6, 5, 16, 1, 0, TimeSpan.Zero));

            await repository.UpsertAsync(firstCapture);
            await repository.UpsertAsync(sameHourCapture);
            await repository.UpsertAsync(nextHourCapture);

            Assert.True(File.Exists(BuildEvidencePath(tempRoot, firstCapture.Id)));
            Assert.False(File.Exists(BuildEvidencePath(tempRoot, sameHourCapture.Id)));
            Assert.True(File.Exists(BuildEvidencePath(tempRoot, nextHourCapture.Id)));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task TryPeekHour_should_return_data_for_an_hour_that_just_closed()
    {
        // The hour reaches disk as soon as its evidence exists; TryPeekHour serves it from the
        // in-memory bucket meanwhile, saving callers the read back.
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var closedHourCapture = CreateArtifact(
                id: "capture-peek-test-1",
                source: "unit-test-peek",
                kind: "capture",
                capturedAtUtc: new DateTimeOffset(2026, 6, 5, 15, 55, 0, TimeSpan.Zero));

            await repository.UpsertAsync(closedHourCapture);

            var summaryPath = Path.Combine(tempRoot, "monitoringArtifacts", "capture-summary-2026-06-05_15.json");
            Assert.True(File.Exists(summaryPath));

            var peeked = repository.TryPeekHour("2026-06-05_15");

            Assert.NotNull(peeked);
            Assert.Contains(peeked.Sources, s => s.SourceId == "unit-test-peek");
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public void TryPeekHour_should_return_null_for_an_hour_with_no_captures()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot };
            var evidenceStore = new FileSystemEvidenceStore(options);
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                evidenceStore,
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            Assert.Null(repository.TryPeekHour("2026-06-05_15"));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ResolveStatus_should_fall_back_to_the_coarse_signal_when_there_is_no_checkpoint_data()
    {
        // Legacy evidence written before per-minute checkpoints existed — nothing to derive a
        // timeline from, so this must keep reading as "ok" rather than "no-session".
        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = CreateRepository(tempRoot);
            var artifact = CreateCaptureArtifact(
                id: "capture-legacy-1",
                source: "legacy-source",
                capturedAtUtc: new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero),
                capturedSeconds: 3600,
                silenceFilledSeconds: 0,
                checkpoints: []);

            await repository.UpsertAsync(artifact);

            var snapshot = repository.TryPeekHour("2026-09-16_23");
            var source = Assert.Single(snapshot!.Sources);
            Assert.Equal("ok", source.Status);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ResolveStatus_should_report_no_session_when_the_worker_was_down_most_of_the_hour()
    {
        // Session only starts 50 minutes in (elapsed=3000) and captures cleanly after that —
        // most of the hour still had no worker running at all.
        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = CreateRepository(tempRoot);
            var artifact = CreateCaptureArtifact(
                id: "capture-late-start-1",
                source: "late-source",
                capturedAtUtc: new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero),
                capturedSeconds: 600,
                silenceFilledSeconds: 0,
                checkpoints: [new WindowCheckpoint(3000, 0, 0)]);

            await repository.UpsertAsync(artifact);

            var snapshot = repository.TryPeekHour("2026-09-16_23");
            var source = Assert.Single(snapshot!.Sources);
            Assert.Equal("no-session", source.Status);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ResolveStatus_should_report_no_session_when_most_of_the_hour_was_bridged()
    {
        // Silence-filled seconds only ever come from the bridge a session writes to cover a span
        // it wasn't capturing, so half an hour of them means this source had no capture running
        // for that half — not that capture ran and degraded.
        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = CreateRepository(tempRoot);
            var artifact = CreateCaptureArtifact(
                id: "capture-silence-1",
                source: "silence-source",
                capturedAtUtc: new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero),
                capturedSeconds: 1810,
                silenceFilledSeconds: 1790,
                checkpoints:
                [
                    new WindowCheckpoint(0, 0, 0),
                    new WindowCheckpoint(1800, 1800, 0),
                ]);

            await repository.UpsertAsync(artifact);

            var snapshot = repository.TryPeekHour("2026-09-16_23");
            var source = Assert.Single(snapshot!.Sources);
            Assert.Equal("no-session", source.Status);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ResolveStatus_should_report_gap_filled_when_capture_ran_all_hour_but_fell_behind()
    {
        // Capture ran the whole hour (nothing bridged) but only got half the audio in the second
        // stretch — degraded coverage, which is what "gap-filled" is for.
        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = CreateRepository(tempRoot);
            var artifact = CreateCaptureArtifact(
                id: "capture-partial-1",
                source: "partial-source",
                capturedAtUtc: new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.Zero),
                capturedSeconds: 2700,
                silenceFilledSeconds: 0,
                checkpoints:
                [
                    new WindowCheckpoint(0, 0, 0),
                    new WindowCheckpoint(1800, 1800, 0),
                ]);

            await repository.UpsertAsync(artifact);

            var snapshot = repository.TryPeekHour("2026-09-16_22");
            var source = Assert.Single(snapshot!.Sources);
            Assert.Equal("gap-filled", source.Status);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ResolveStatus_should_report_ok_when_the_worker_ran_and_captured_the_full_hour()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = CreateRepository(tempRoot);
            var artifact = CreateCaptureArtifact(
                id: "capture-ok-1",
                source: "ok-source",
                capturedAtUtc: new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero),
                capturedSeconds: 3600,
                silenceFilledSeconds: 0,
                checkpoints:
                [
                    new WindowCheckpoint(0, 0, 0),
                    new WindowCheckpoint(1800, 1800, 0),
                ]);

            await repository.UpsertAsync(artifact);

            var snapshot = repository.TryPeekHour("2026-09-16_23");
            var source = Assert.Single(snapshot!.Sources);
            Assert.Equal("ok", source.Status);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static StageMirrorMonitoringArtifactRepository CreateRepository(string tempRoot)
    {
        var options = new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot };
        var evidenceStore = new FileSystemEvidenceStore(options);
        return new StageMirrorMonitoringArtifactRepository(
            new InMemoryMonitoringArtifactRepository(),
            evidenceStore,
            Array.Empty<IMonitoringArtifactDatabaseRepository>());
    }

    private static MonitoringArtifact CreateCaptureArtifact(
        string id,
        string source,
        DateTimeOffset capturedAtUtc,
        double capturedSeconds,
        double silenceFilledSeconds,
        WindowCheckpoint[] checkpoints)
    {
        var checkpointsJson = string.Join(",", checkpoints.Select(c =>
            $"{{\"ElapsedSeconds\":{c.ElapsedSeconds},\"CapturedSeconds\":{c.CapturedSeconds},\"SilenceFilledSeconds\":{c.SilenceFilledSeconds}}}"));
        var payload =
            $"{{\"Succeeded\":true,\"OpusFilePath\":\"{source}.opus\",\"CapturedSeconds\":{capturedSeconds}," +
            $"\"SilenceFilledSeconds\":{silenceFilledSeconds},\"Checkpoints\":[{checkpointsJson}]}}";

        return new MonitoringArtifact(
            id: id,
            tenantId: "global-ingestion",
            source: source,
            kind: "capture",
            payloadJson: payload,
            capturedAtUtc: capturedAtUtc);
    }

    [Fact]
    public async Task UpsertAsync_should_persist_the_hour_summary_on_the_very_first_capture_artifact()
    {
        // Regression: the summary used to be written only when a source crossed into the next
        // hour. That trigger never fires on the first rotation of a run, so a worker restarted
        // within the hour left nothing on disk and the status page lost the hour entirely.
        var tempRoot = CreateTempDirectory();
        try
        {
            var options = new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot };
            var repository = new StageMirrorMonitoringArtifactRepository(
                new InMemoryMonitoringArtifactRepository(),
                new FileSystemEvidenceStore(options),
                Array.Empty<IMonitoringArtifactDatabaseRepository>());

            var capturedAt = new DateTimeOffset(2026, 9, 17, 13, 0, 0, TimeSpan.Zero);
            await repository.UpsertAsync(CreateArtifact("artifact-first-hour", capturedAtUtc: capturedAt));

            var summaryPath = Path.Combine(
                tempRoot, "monitoringArtifacts", "capture-summary-2026-09-17_13.json");
            Assert.True(File.Exists(summaryPath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static MonitoringArtifact CreateArtifact(
        string id,
        string source = "unit-test",
        string kind = "capture",
        DateTimeOffset? capturedAtUtc = null)
    {
        return new MonitoringArtifact(
            id: id,
            tenantId: "global-ingestion",
            source: source,
            kind: kind,
            payloadJson: "{}",
            capturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static string BuildEvidencePath(string root, string artifactId)
    {
        var escapedId = Uri.EscapeDataString(artifactId);
        return Path.Combine(root, "monitoringArtifacts", $"{escapedId}.json");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stage-mirror-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SuccessfulDatabaseRepository : IMonitoringArtifactDatabaseRepository
    {
        public int UpsertCalls { get; private set; }

        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            return Task.CompletedTask;
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MonitoringArtifact?>(null);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(Array.Empty<MonitoringArtifact>());
        }
    }

    private sealed class FailingDatabaseRepository : IMonitoringArtifactDatabaseRepository
    {
        public Task UpsertAsync(MonitoringArtifact artifact, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("DB unavailable");
        }

        public Task<MonitoringArtifact?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MonitoringArtifact?>(null);
        }

        public Task<IReadOnlyList<MonitoringArtifact>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoringArtifact>>(Array.Empty<MonitoringArtifact>());
        }
    }
}
