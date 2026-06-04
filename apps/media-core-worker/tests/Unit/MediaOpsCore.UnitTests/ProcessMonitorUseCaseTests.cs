using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class ProcessMonitorUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_should_restart_non_running_active_processes()
    {
        var repository = new InMemoryProcessStateRepository(new[]
        {
            new ProcessState("radio", "news", "capture", 77, "echo restart", new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), null, "Active", "c:/tmp/file.mp3")
        });

        var useCase = new ProcessMonitorUseCase(
            repository,
            new FakeProcessInspector(_ => false),
            new SuccessfulRunner(),
            new ProcessGuardianOptions
            {
                RestartTimeout = TimeSpan.FromMinutes(30),
                RestartCommandTimeout = TimeSpan.FromSeconds(5)
            },
            utcNow: () => new DateTimeOffset(2026, 6, 3, 10, 31, 0, TimeSpan.Zero));

        var result = await useCase.ExecuteAsync();
        var updated = (await repository.ListAsync()).Single();

        Assert.Equal(1, result.Inspected);
        Assert.Equal(1, result.Restarted);
        Assert.Equal(1, result.TimedOut);
        Assert.Equal("Restarted", updated.Status);
    }

    private sealed class SuccessfulRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult(0, "ok", string.Empty, false));
        }
    }

    private sealed class FakeProcessInspector : IProcessInspector
    {
        private readonly Func<int, bool> isRunning;

        public FakeProcessInspector(Func<int, bool> isRunning)
        {
            this.isRunning = isRunning;
        }

        public bool IsRunning(int processId) => isRunning(processId);

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