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
    public async Task ListAllAsync_should_return_only_primary_sources_when_primary_succeeds()
    {
        // Firestore is the single source of truth once it answers — the JSON file is not merged
        // in, even though it holds other sources, so it never produces a duplicate capture.
        var primary = new StubRepository([MakeSource("a"), MakeSource("b")]);
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Equal(["a", "b"], result.Select(s => s.SourceId).Order());
        Assert.False(secondary.WasCalled);
    }

    [Fact]
    public async Task ListAllAsync_should_return_an_empty_primary_result_as_is_without_falling_back()
    {
        // An empty-but-successful response is a real answer ("no sources configured"), not a
        // connection failure — it must not trigger a fallback to the JSON file.
        var primary = new StubRepository([]);
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Empty(result);
        Assert.False(secondary.WasCalled);
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
    public async Task UpdateStreamUrlAsync_should_write_to_primary_when_primary_is_available()
    {
        var primary = new StubRepository([MakeSource("a")]);
        var secondary = new StubRepository([MakeSource("a")]);
        var repository = Build(primary, secondary);
        await repository.ListAllAsync();

        var changed = await repository.UpdateStreamUrlAsync("a", "https://new.example.com/stream");

        Assert.True(changed);
        Assert.True(primary.UpdateStreamUrlWasCalled);
        Assert.False(secondary.UpdateStreamUrlWasCalled);
    }

    [Fact]
    public async Task UpdateStreamUrlAsync_should_write_to_secondary_once_primary_is_unreachable()
    {
        var primary = new ThrowingRepository(new HttpRequestException("primary failed"));
        var secondary = new StubRepository([MakeSource("a")]);
        var repository = Build(primary, secondary);
        await repository.ListAllAsync();

        var changed = await repository.UpdateStreamUrlAsync("a", "https://new.example.com/stream");

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
