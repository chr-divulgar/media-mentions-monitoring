namespace MediaOpsCore.Modules.ProcessGuardian.Application;

public sealed class ReconcileInactiveUseCase : IReconcileInactiveUseCase
{
    private readonly IProcessStateRepository processStateRepository;
    private readonly Func<DateTimeOffset> utcNow;

    public ReconcileInactiveUseCase(IProcessStateRepository processStateRepository, Func<DateTimeOffset>? utcNow = null)
    {
        this.processStateRepository = processStateRepository;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ReconcileInactiveResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var states = await processStateRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var pending = states
            .Where(state => string.Equals(state.Status, "Inactive", StringComparison.OrdinalIgnoreCase) && state.EndedAtUtc is null)
            .ToArray();

        foreach (var state in pending)
        {
            var reconciled = state with
            {
                EndedAtUtc = utcNow(),
                Status = "ReconciledInactive"
            };
            await processStateRepository.UpsertAsync(reconciled, cancellationToken).ConfigureAwait(false);
        }

        return new ReconcileInactiveResult(pending.Length);
    }
}