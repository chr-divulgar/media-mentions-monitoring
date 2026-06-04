namespace MediaOpsCore.BuildingBlocks.Application;

public sealed class ProcessCommand
{
    public ProcessCommand(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null)
    {
        FileName = string.IsNullOrWhiteSpace(fileName) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(fileName)) : fileName;
        Arguments = arguments?.ToArray() ?? throw new ArgumentNullException(nameof(arguments));
        WorkingDirectory = workingDirectory;
        EnvironmentVariables = environmentVariables;
        Timeout = timeout;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; }

    public TimeSpan? Timeout { get; }
}