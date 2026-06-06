namespace MediaOpsCore.Workers.Operations;

public sealed record StartupStreamValidationResult(bool Succeeded, string? ErrorMessage = null);
