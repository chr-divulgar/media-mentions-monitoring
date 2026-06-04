using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed class ProcessMonitorUseCase : IProcessMonitorUseCase
{
    private readonly IProcessStateRepository processStateRepository;
    private readonly IProcessInspector processInspector;
    private readonly IProcessRunner processRunner;
    private readonly ProcessGuardianOptions options;
    private readonly Func<DateTimeOffset> utcNow;

    public ProcessMonitorUseCase(
        IProcessStateRepository processStateRepository,
        IProcessInspector processInspector,
        IProcessRunner processRunner,
        ProcessGuardianOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.processStateRepository = processStateRepository;
        this.processInspector = processInspector;
        this.processRunner = processRunner;
        this.options = options;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ProcessMonitorResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var states = await processStateRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var activeStates = states
            .Where(state => string.Equals(state.Status, "Active", StringComparison.OrdinalIgnoreCase) && !string.Equals(state.ProcessType, "chunk", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var restarted = 0;
        var timedOut = 0;

        foreach (var state in activeStates)
        {
            var age = utcNow() - state.StartedAtUtc;
            var isRunning = processInspector.IsRunning(state.ProcessId);
            var mustRestart = !isRunning || age >= options.RestartTimeout;

            if (!mustRestart)
            {
                continue;
            }

            if (age >= options.RestartTimeout)
            {
                timedOut++;
            }

            if (isRunning)
            {
                processInspector.TryStop(state.ProcessId);
            }

            var restartCommand = new ProcessCommand(
                fileName: "cmd.exe",
                arguments: new[] { "/c", state.Command },
                timeout: options.RestartCommandTimeout);

            var restartResult = await processRunner.RunAsync(restartCommand, cancellationToken).ConfigureAwait(false);
            if (!restartResult.Succeeded)
            {
                continue;
            }

            restarted++;
            var updatedState = state with
            {
                StartedAtUtc = utcNow(),
                EndedAtUtc = null,
                Status = "Restarted"
            };
            await processStateRepository.UpsertAsync(updatedState, cancellationToken).ConfigureAwait(false);
        }

        return new ProcessMonitorResult(activeStates.Length, restarted, timedOut);
    }
}