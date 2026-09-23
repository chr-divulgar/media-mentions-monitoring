namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Minimal .env loader, run once at startup before OperationsWorkerOptionsLoader reads any
/// environment variables (e.g. FIREBASE_BASE_URL). Nothing in this project previously loaded
/// .env files — Environment.GetEnvironmentVariable only ever saw real process environment
/// variables, so a .env file sitting on disk was silently inert regardless of where it lived or
/// how the worker was launched (`dotnet run`, the built .dll directly, a service, ...).
///
/// Searches upward from the running assembly's own directory rather than assuming one fixed
/// location, so it works the same way regardless of the current working directory the process
/// happened to start from.
/// </summary>
public static class DotEnvLoader
{
    private const int MaxDirectoriesToSearch = 6;

    public static void LoadIfPresent()
    {
        var envFilePath = FindEnvFile();
        if (envFilePath is null)
        {
            return;
        }

        var parsed = ParseEnvFile(File.ReadAllLines(envFilePath));
        var loadedCount = 0;
        foreach (var (key, value) in parsed)
        {
            if (Environment.GetEnvironmentVariable(key) is not null)
            {
                // A real environment variable (set by the shell, a service, launchSettings, ...)
                // always takes precedence over the .env file.
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            loadedCount++;
        }

        Console.WriteLine($"[DotEnvLoader] Loaded {loadedCount} variable(s) from {envFilePath}.");
    }

    // Pure parsing, no I/O or environment mutation — kept separate so it can be unit tested
    // directly without touching real process environment state or the filesystem.
    internal static IReadOnlyDictionary<string, string> ParseEnvFile(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');

            if (key.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string? FindEnvFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < MaxDirectoriesToSearch && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
