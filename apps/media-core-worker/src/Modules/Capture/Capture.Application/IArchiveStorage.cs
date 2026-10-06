namespace MediaOpsCore.Modules.Capture.Application;

/// <summary>
/// Long-term home for closed capture files (RQ-070/RQ-071). The local disk is only a rolling
/// buffer; whatever is behind this port is the archive. FTP today, a cloud bucket later — the
/// "what to upload, when it is safe to delete locally" logic never knows which.
/// </summary>
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

    // Copies relativePath to localPath and returns true only once the local copy has the remote size.
    Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default);
}
