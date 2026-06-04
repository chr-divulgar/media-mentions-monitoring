namespace MediaOpsCore.BuildingBlocks.Application.Abstractions.Persistence;

public sealed record ProcessState(
    string Platform,
    string Media,
    string ProcessType,
    int ProcessId,
    string Command,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Status,
    string? SourceFilePath);