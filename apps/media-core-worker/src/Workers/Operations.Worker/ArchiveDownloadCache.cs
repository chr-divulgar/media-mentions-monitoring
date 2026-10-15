using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// A local folder holding files fetched back from the archive (IArchiveStorage) so an old recording is
/// downloaded once and then served from disk: the TV video of an earlier day (video-cache) and the radio
/// hour as mp3 (audio-cache). A file is kept for MaxAge and the folder is capped in size, oldest first.
/// </summary>
public sealed class ArchiveDownloadCache
{
    private readonly IArchiveStorage storage;
    private readonly string root;
    private readonly TimeSpan maxAge;
    private readonly long maxBytes;
    private readonly ILogger logger;

    // One download at a time: they are tens or hundreds of MB, and two editors opening the same alert must
    // not fetch the same file twice.
    private readonly SemaphoreSlim gate = new(1, 1);

    public ArchiveDownloadCache(IArchiveStorage storage, string root, TimeSpan maxAge, long maxBytes, ILogger logger)
    {
        this.storage = storage;
        this.root = root;
        this.maxAge = maxAge;
        this.maxBytes = maxBytes;
        this.logger = logger;
    }

    // Where the archived file lives locally once it has been fetched.
    public string PathFor(string relativePath) => Path.Combine(root, relativePath);

    // True when the file is already here and has not outlived MaxAge: no need to ask the server anything.
    public bool IsFresh(string relativePath)
    {
        var cached = PathFor(relativePath);
        return File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < maxAge;
    }

    // Makes sure the archived file is in the cache, downloading it when it is not there or has outlived
    // MaxAge. Returns false when the download failed.
    public async Task<bool> EnsureAsync(string relativePath, CancellationToken cancellationToken)
    {
        var cached = PathFor(relativePath);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsFresh(relativePath))
            {
                return true;
            }

            // Into a .part file: a download cut short must never be mistaken for the file.
            var partial = cached + ".part";
            bool downloaded;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cached))!);
                downloaded = await storage.DownloadAsync(relativePath, partial, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[ArchiveDownloadCache] Could not download {Path} from the archive.", relativePath);
                downloaded = false;
            }

            if (!downloaded)
            {
                TryDelete(partial);
                return false;
            }

            File.Move(partial, cached, overwrite: true);
            // A file's age is when it was fetched, whatever modification time the download carried over.
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            TrimCache(keep: cached);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // Drops cached files past their age, then the oldest ones while the folder is over its cap.
    private void TrimCache(string keep)
    {
        var files = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !file.Name.EndsWith(".part", StringComparison.Ordinal)
                && !string.Equals(file.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(file => file.Length) + new FileInfo(keep).Length;

        foreach (var file in files)
        {
            if (DateTime.UtcNow - file.LastWriteTimeUtc < maxAge && total <= maxBytes)
            {
                break;
            }

            total -= file.Length;
            TryDelete(file.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover file is cleaned by a later pass.
        }
    }
}
