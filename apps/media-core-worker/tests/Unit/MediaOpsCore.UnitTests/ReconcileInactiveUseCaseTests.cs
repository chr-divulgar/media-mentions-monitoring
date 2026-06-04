using MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ReconcileInactiveUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_set_end_time_for_inactive_processes_without_end_time()
    {
        var repository = new InMemoryProcessStateRepository(new[]
        {
            new ProcessState("radio", "news", "capture", 77, "echo restart", new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), null, "Inactive", "c:/tmp/file.mp3")
        });

        var useCase = new ReconcileInactiveUseCase(
            repository,
            utcNow: () => new DateTimeOffset(2026, 6, 3, 10, 40, 0, TimeSpan.Zero));

        var result = await useCase.ExecuteAsync();
        var updated = (await repository.ListAsync()).Single();

        Assert.Equal(1, result.Reconciled);
        Assert.Equal("ReconciledInactive", updated.Status);
        Assert.NotNull(updated.EndedAtUtc);
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