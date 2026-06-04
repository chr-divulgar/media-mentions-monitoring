using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class FileSystemEvidenceStore : IEvidenceFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string rootPath;

    public FileSystemEvidenceStore(OperationsWorkerOptions options)
    {
        rootPath = string.IsNullOrWhiteSpace(options.StageFilesystemRootPath)
            ? throw new ArgumentException("StageFilesystemRootPath cannot be empty.", nameof(options))
            : options.StageFilesystemRootPath;
    }

    public async Task WriteJsonAsync<T>(string relativePath, T payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(relativePath));
        }

        var normalizedRelative = relativePath.Replace('\\', '/');
        var outputPath = Path.Combine(rootPath, normalizedRelative);
        var directory = Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(relativePath));
        }

        var normalizedRelative = relativePath.Replace('\\', '/');
        var outputPath = Path.Combine(rootPath, normalizedRelative);

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        return Task.CompletedTask;
    }
}