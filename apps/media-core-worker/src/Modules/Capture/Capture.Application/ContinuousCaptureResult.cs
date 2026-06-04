namespace MediaOpsCore.Modules.Capture.Application;

public sealed record ContinuousCaptureResult(int Attempts, int Succeeded, int Failed, DateTimeOffset? LastCapturedAtUtc);