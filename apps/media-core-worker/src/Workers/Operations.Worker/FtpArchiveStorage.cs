using FluentFTP;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>ARCHIVE_FTP_URL / _USER / _PASSWORD, parsed. Never read from worker-options.json.</summary>
public sealed record FtpArchiveCredentials(string Url, string User, string Password)
{
    // ftp://host[:port]/base/path  — ftps:// means implicit TLS (default port 990).
    internal (string Host, int Port, string BasePath, bool Implicit) Parse()
    {
        var uri = new Uri(Url);
        var isImplicit = uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase);
        var port = uri.IsDefaultPort || uri.Port <= 0 ? (isImplicit ? 990 : 21) : uri.Port;
        return (uri.Host, port, uri.AbsolutePath.TrimEnd('/'), isImplicit);
    }
}

/// <summary>
/// IArchiveStorage over FTP/FTPS with FluentFTP — fully managed .NET, so it runs the same on this
/// Windows box, on Linux or in a container, with nothing installed (no FileZilla, no curl).
/// A connection per upload: each file is tens of MB, so the handshake is noise, and it keeps
/// parallel uploads independent (an FtpClient is not safe for concurrent transfers).
/// </summary>
public sealed class FtpArchiveStorage : IArchiveStorage
{
    private readonly FtpArchiveCredentials credentials;
    private readonly bool requireTls;
    private readonly bool allowInvalidCertificate;
    private readonly ILogger<FtpArchiveStorage> logger;

    public FtpArchiveStorage(OperationsWorkerOptions options, ILogger<FtpArchiveStorage> logger)
    {
        credentials = options.ArchiveFtp ?? throw new InvalidOperationException("ARCHIVE_FTP_* is not configured.");
        requireTls = options.ArchiveFtpRequireTls;
        allowInvalidCertificate = options.ArchiveFtpAllowInvalidCertificate;
        this.logger = logger;
    }

    public async Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var (host, port, basePath, isImplicit) = credentials.Parse();
        var remotePath = $"{basePath}/{relativePath.TrimStart('/')}";

        try
        {
            await using var client = new AsyncFtpClient(host, credentials.User, credentials.Password, port);
            client.Config.EncryptionMode = isImplicit
                ? FtpEncryptionMode.Implicit
                : requireTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
            client.Config.ValidateAnyCertificate = allowInvalidCertificate;
            client.Config.RetryAttempts = 2;

            await client.Connect(cancellationToken).ConfigureAwait(false);

            var status = await client.UploadFile(
                localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
            {
                logger.LogWarning("[FtpArchiveStorage] Upload of {Path} reported failure.", relativePath);
                return false;
            }

            // Verified, not assumed: only a remote copy as long as the local file counts as archived.
            var remoteSize = await client.GetFileSize(remotePath, -1, cancellationToken).ConfigureAwait(false);
            var localSize = new FileInfo(localPath).Length;
            if (remoteSize != localSize)
            {
                logger.LogWarning("[FtpArchiveStorage] {Path}: remote size {Remote} != local {Local}.", relativePath, remoteSize, localSize);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[FtpArchiveStorage] Could not archive {Path}.", relativePath);
            return false;
        }
    }
}
