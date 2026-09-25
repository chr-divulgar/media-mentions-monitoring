using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Sends an already-persisted alert out (WhatsApp today). Best-effort by contract: implementations
// must not throw for expected/operational failures (sidecar unreachable, request timeout) — log
// and return instead, the same way the rest of this module treats notification as something that
// must never take down alert detection/persistence. Only truly unexpected errors may propagate.
public interface IAlertNotifier
{
    // Returns the subset of `recipients` actually confirmed sent — callers persist that subset
    // so a retry only targets whoever is left, instead of resending to everyone.
    Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default);
}
