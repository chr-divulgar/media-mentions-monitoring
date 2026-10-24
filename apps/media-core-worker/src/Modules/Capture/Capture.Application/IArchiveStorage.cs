namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Long-term home for closed capture files (RQ-070/RQ-071). The local disk is only a rolling
/// buffer; whatever is behind this port is the archive. FTP today, a cloud bucket later — the
/// "what to upload, when it is safe to delete locally" logic never knows which.
/// </summary>
/// <summary>The media the archive keeps apart, each with its own account on the server.</summary>
public static class ArchiveMedia
{
    public const string Radio = "radio";
    public const string Television = "television";
    // The press: the PDF copies of the notes of internet.
    public const string Press = "press";

    // The medium a file of the archive belongs to, from the first folder of its path (radio/…, television/…,
    // notes/…); null for anything else.
    public static string? MediumOf(string relativePath) => relativePath.Split('/', 2)[0] switch
    {
        "radio" => Radio,
        "television" => Television,
        "notes" => Press,
        _ => null,
    };
}

/// <summary>
/// The archive storage of each medium. Radio, television and press each have their own account, so a fault or a
/// deletion in one never reaches the others; a medium with no account gets null.
/// </summary>
public interface IArchiveStorageProvider
{
    IArchiveStorage? For(string medium);
}

public interface IArchiveStorage
{
    // Uploads localPath to relativePath ('/'-separated, mirroring the local layout) and returns
    // true only once the remote copy is verified to be complete. Must not throw for operational
    // failures (server down, credentials, network): return false and let the caller retry.
    Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default);

    // File names (not paths) directly inside relativeDirectory; empty when the directory does not
    // exist. Unlike UploadAsync this throws on operational failures: "nothing there" and "could not
    // look" must not be confused by a reader that is about to tell a user the file does not exist.
    Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default);

    // Names (not paths) of the folders directly inside relativeDirectory; empty when it does not exist. Like
    // ListAsync it throws on operational failures, so "nothing there" is never confused with "could not look".
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default);

    // Deletes relativeDirectory and everything under it. Irreversible, and the archive's base folder may be
    // shared with other things, so callers decide WHICH folders (see ArchiveRemoteRetention) and this only
    // refuses the obviously wrong (empty, rooted, or going up a level). Throws on operational failures.
    Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default);

    // Deletes one file (the file of a deleted note). True once it is not there any more, also when it never was; false when
    // this storage cannot delete files. Throws on operational failures, like DeleteDirectoryAsync.
    Task<bool> DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(false);

    // Copies relativePath to localPath and returns true only once the local copy has the remote size.
    Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default);
}
