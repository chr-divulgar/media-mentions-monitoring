namespace MediaOpsCore.Workers.Operations;

public sealed record CanaryTuningDecision(int PreviousPercent, int CurrentPercent, bool Increased, bool Decreased, string Reason);