using MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ChunkProcessMonitorUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_stop_orphan_chunk_processes()
    {
        var repository = new InMemoryProcessStateRepository(new[]
        {
            new ProcessState("radio", "news", "chunk", 77, "echo restart", new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), null, "Active", null)
        });

        var useCase = new ChunkProcessMonitorUseCase(
            repository,
            new AlwaysRunningInspector(),
            utcNow: () => new DateTimeOffset(2026, 6, 3, 10, 45, 0, TimeSpan.Zero));

        var result = await useCase.ExecuteAsync();
        var updated = (await repository.ListAsync()).Single();

        Assert.Equal(1, result.OrphansDetected);
        Assert.Equal(1, result.OrphansStopped);
        Assert.Equal("OrphanStopped", updated.Status);
    }

    private sealed class AlwaysRunningInspector : IProcessInspector
    {
        public bool IsRunning(int processId) => true;

        public bool TryStop(int processId) => true;
    }

    private sealed class InMemoryProcessStateRepository : IProcessStateRepository
    {
        private readonly List<ProcessState> states;

        public InMemoryProcessStateRepository(IEnumerable<ProcessState> states)
        {
            this.states = states.ToList();
        }

        public Task<IReadOnlyList<ProcessState>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProcessState>>(states.ToArray());
        }

        public Task UpsertAsync(ProcessState state, CancellationToken cancellationToken = default)
        {
            states.RemoveAll(existing => existing.ProcessId == state.ProcessId && existing.ProcessType == state.ProcessType);
            states.Add(state);
            return Task.CompletedTask;
        }
    }
}