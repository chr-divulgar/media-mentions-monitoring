using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FallbackCaptureSourceRepositoryTests
{
    private static CaptureSource MakeSource(string id) =>
        new(id, "global-ingestion", "P", "radio", "https://example.com/stream");

    private static FallbackCaptureSourceRepository Build(
        ICaptureSourceRepository primary, ICaptureSourceRepository secondary) =>
        new(primary, secondary, NullLogger<FallbackCaptureSourceRepository>.Instance);

    [Fact]
    public async Task ListAllAsync_should_return_primary_sources_when_primary_succeeds()
    {
        var primary = new StubRepository([MakeSource("a"), MakeSource("b")]);
        var secondary = new StubRepository([MakeSource("c")]);

        var result = await Build(primary, secondary).ListAllAsync();

        Assert.Equal(2, result.Count);
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
    public async Task ListAllAsync_should_propagate_secondary_exception_when_both_fail()
    {
        var primary = new ThrowingRepository(new HttpRequestException("primary failed"));
        var secondary = new ThrowingRepository(new FileNotFoundException("secondary failed"));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Build(primary, secondary).ListAllAsync());
    }

    [Fact]
    public async Task ListAllAsync_should_not_call_secondary_when_primary_returns_non_empty()
    {
        var primary = new StubRepository([MakeSource("a")]);
        var secondary = new StubRepository([MakeSource("b")]);

        await Build(primary, secondary).ListAllAsync();

        Assert.False(secondary.WasCalled);
    }

    private sealed class StubRepository : ICaptureSourceRepository
    {
        private readonly IReadOnlyList<CaptureSource> sources;
        public bool WasCalled { get; private set; }
        public StubRepository(IReadOnlyList<CaptureSource> sources) => this.sources = sources;
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(sources);
        }
    }

    private sealed class ThrowingRepository : ICaptureSourceRepository
    {
        private readonly Exception exception;
        public ThrowingRepository(Exception exception) => this.exception = exception;
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken ct = default) =>
            throw exception;
    }
}
