using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FallbackCaptureSourceRepositoryTests
{
    private static CaptureSource MakeSource(string id) =>
        new(id, "global-ingestion", "P", "radio", $"https://example.com/{id}/stream");

    private static FallbackCaptureSourceRepository Build(
        ICaptureSourceRepository primary, ICaptureSourceRepository secondary) =>
        new(primary, secondary, NullLogger<FallbackCaptureSourceRepository>.Instance);

    [Fact]
    public async Task ListAllAsync_should_union_primary_and_secondary_sources_by_SourceId()
    {
        // The two catalogs cover different, non-overlapping stations in practice — a real
        // fallback (either/or) would silently drop whichever one didn't "win", so this must be a
        // union, not a choice.
        var primary = new StubRepository([MakeSource("a"), MakeSource("b")]);
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, s => s.SourceId == "a");
        Assert.Contains(result, s => s.SourceId == "b");
        Assert.Contains(result, s => s.SourceId == "c");
        Assert.True(secondary.WasCalled);
    }

    [Fact]
    public async Task ListAllAsync_should_drop_a_secondary_source_that_streams_the_same_url_as_a_primary_one()
    {
        // Same station in both catalogs under different ids: recording both doubled every file and
        // alert, and the server kept cutting the duplicate connection.
        var primary = new StubRepository([new CaptureSource("Colmundo", "global-ingestion", "P", "radio", "https://stream.example.com/colmundo/")]);
        var secondary = new StubRepository([
            new CaptureSource("colmundo-radio-bogota", "global-ingestion", "P", "radio", "http://stream.example.com/colmundo"),
            MakeSource("only-in-json"),
        ]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Equal(["Colmundo", "only-in-json"], result.Select(s => s.SourceId).Order());
    }

    [Fact]
    public async Task ListAllAsync_should_prefer_primary_on_a_SourceId_collision()
    {
        var primary = new StubRepository([new CaptureSource("a", "global-ingestion", "P", "radio", "https://primary.example.com/stream")]);
        var secondary = new StubRepository([new CaptureSource("a", "global-ingestion", "P", "radio", "https://secondary.example.com/stream")]);

        var result = await Build(primary, secondary).ListAllAsync();

        var merged = Assert.Single(result);
        Assert.Equal("https://primary.example.com/stream", merged.StreamUrl);
    }

    [Fact]
    public async Task ListAllAsync_should_call_secondary_when_primary_throws_HttpRequestException()
    {
        var primary = new ThrowingRepository(new HttpRequestException("connection refused"));
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Single(result);
        Assert.Equal("c", result[0].SourceId);
    }

    [Fact]
    public async Task ListAllAsync_should_call_secondary_when_primary_returns_zero_sources()
    {
        var primary = new StubRepository([]);
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Single(result);
        Assert.True(secondary.WasCalled);
    }

    [Fact]
    public async Task ListAllAsync_should_propagate_OperationCanceledException_when_caller_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primary = new ThrowingRepository(new OperationCanceledException(cts.Token));
        var secondary = new StubRepository([MakeSource("c")]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Build(primary, secondary).ListAllAsync(cts.Token));

        Assert.False(secondary.WasCalled);
    }

    [Fact]
    public async Task ListAllAsync_should_call_secondary_when_primary_times_out_with_internal_token()
    {
        // Simulate an internal timeout (different CancellationToken from caller's)
        using var internalCts = new CancellationTokenSource();
        internalCts.Cancel();
        var primary = new ThrowingRepository(new OperationCanceledException(internalCts.Token));
        var secondary = new StubRepository([MakeSource("c")]);
        using var callerCts = new CancellationTokenSource();

        var result = await Build(primary, secondary).ListAllAsync(callerCts.Token);

        Assert.Single(result);
        Assert.True(secondary.WasCalled);
    }

    [Fact]
    public async Task ListAllAsync_should_return_an_empty_list_when_both_repositories_fail()
    {
        var primary = new ThrowingRepository(new HttpRequestException("primary failed"));
        var secondary = new ThrowingRepository(new FileNotFoundException("secondary failed"));

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task UpdateStreamUrlAsync_should_write_only_to_the_repository_that_owns_the_source()
    {
        // Writing a JSON-only station to Firestore cost a query per hot-recovery attempt.
        var primary = new StubRepository([MakeSource("firestore-only")]);
        var secondary = new StubRepository([MakeSource("json-only")]);
        var repository = Build(primary, secondary);
        await repository.ListAllAsync();

        var changed = await repository.UpdateStreamUrlAsync("json-only", "https://new.example.com/stream");

        Assert.True(changed);
        Assert.False(primary.UpdateStreamUrlWasCalled);
        Assert.True(secondary.UpdateStreamUrlWasCalled);
    }

    [Fact]
    public async Task UpdateStreamUrlAsync_should_write_to_secondary_even_when_primary_succeeds()
    {
        var primary = new StubRepository([MakeSource("a")]);
        var secondary = new StubRepository([MakeSource("a")]);

        var changed = await Build(primary, secondary)
            .UpdateStreamUrlAsync("a", "https://new.example.com/stream");

        Assert.True(changed);
        Assert.True(primary.UpdateStreamUrlWasCalled);
        Assert.True(secondary.UpdateStreamUrlWasCalled);
    }

    [Fact]
    public async Task UpdateStreamUrlAsync_should_return_true_when_primary_fails_and_secondary_succeeds()
    {
        var primary = new ThrowingRepository(new HttpRequestException("primary failed"));
        var secondary = new StubRepository([MakeSource("a")]);

        var changed = await Build(primary, secondary)
            .UpdateStreamUrlAsync("a", "https://new.example.com/stream");

        Assert.True(changed);
        Assert.True(secondary.UpdateStreamUrlWasCalled);
    }

    private sealed class StubRepository : ICaptureSourceRepository
    {
        private readonly IReadOnlyList<CaptureSource> sources;
        public bool WasCalled { get; private set; }
        public bool UpdateStreamUrlWasCalled { get; private set; }
        public StubRepository(IReadOnlyList<CaptureSource> sources) => this.sources = sources;
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(sources);
        }

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default)
        {
            UpdateStreamUrlWasCalled = true;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingRepository : ICaptureSourceRepository
    {
        private readonly Exception exception;
        public ThrowingRepository(Exception exception) => this.exception = exception;
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken ct = default) =>
            throw exception;

        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) =>
            throw exception;
    }
}
