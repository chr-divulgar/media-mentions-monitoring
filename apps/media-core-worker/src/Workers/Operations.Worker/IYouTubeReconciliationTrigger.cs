namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Triggers an out-of-band reconciliation pass for currently-excluded YouTube (TV) sources
/// instead of waiting for the next scheduled reconciliation tick (minutes 0/1/30/59) or an
/// in-flight hot-recovery loop's next per-minute attempt.
/// </summary>
public interface IYouTubeReconciliationTrigger
{
    /// <summary>
    /// Attempts to recover every currently-excluded YouTube source right now.
    /// Returns the number of sources recovered.
    /// </summary>
    Task<int> TriggerImmediateReconciliationAsync(CancellationToken cancellationToken = default);
}
