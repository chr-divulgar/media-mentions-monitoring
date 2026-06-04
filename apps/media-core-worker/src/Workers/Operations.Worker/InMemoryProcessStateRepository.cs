using MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;
using MediaOpsCore.Modules.ProcessGuardian.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class InMemoryProcessStateRepository : IProcessStateRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, ProcessState> states = new(StringComparer.Ordinal);
    private const string DefaultPlatform = "unknown";
    private const string DefaultMedia = "unknown";

    public InMemoryProcessStateRepository()
    {
        var now = DateTimeOffset.UtcNow;
        states["capture-main"] = new ProcessState(
            Platform: DefaultPlatform,
            Media: DefaultMedia,
            ProcessType: "capture",
            ProcessId: Environment.ProcessId,
            Command: "echo restart capture",
            StartedAtUtc: now.AddMinutes(-1),
            EndedAtUtc: null,
            Status: "Active",
            SourceFilePath: null);

        states["chunk-orphan"] = new ProcessState(
            Platform: DefaultPlatform,
            Media: DefaultMedia,
            ProcessType: "chunk",
            ProcessId: Environment.ProcessId,
            Command: "echo restart chunk",
            StartedAtUtc: now.AddMinutes(-2),
            EndedAtUtc: null,
            Status: "Active",
            SourceFilePath: null);
    }

    public Task<IReadOnlyList<ProcessState>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<ProcessState>>(states.Values.ToArray());
        }
    }

    public Task UpsertAsync(ProcessState state, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(state);

        lock (sync)
        {
            states[key] = state;
        }

        return Task.CompletedTask;
    }

    private static string BuildKey(ProcessState state)
    {
        return string.Concat(state.Platform, "::", state.Media, "::", state.ProcessType, "::", state.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}