using System.Security.Cryptography.X509Certificates;
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
    private readonly string? certificateHost;
    private readonly ILogger<FtpArchiveStorage> logger;

    public FtpArchiveStorage(FtpArchiveCredentials credentials, OperationsWorkerOptions options, ILogger<FtpArchiveStorage> logger)
    {
        this.credentials = credentials;
        requireTls = options.ArchiveFtpRequireTls;
        allowInvalidCertificate = options.ArchiveFtpAllowInvalidCertificate;
        certificateHost = options.ArchiveFtpCertificateHost;
        this.logger = logger;
    }

    private static X509Certificate2? ToCertificate2(X509Certificate? certificate) =>
        certificate is null ? null : new X509Certificate2(certificate);

    private (AsyncFtpClient Client, string BasePath) CreateClient()
    {
        var (host, port, basePath, isImplicit) = credentials.Parse();
        var client = new AsyncFtpClient(host, credentials.User, credentials.Password, port);
        client.Config.EncryptionMode = isImplicit
            ? FtpEncryptionMode.Implicit
            : requireTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.ValidateAnyCertificate = allowInvalidCertificate;
        client.ValidateCertificate += (_, e) =>
        {
            e.Accept = allowInvalidCertificate
                || FtpCertificateValidation.ShouldAccept(e.PolicyErrors, e.Certificate as X509Certificate2 ?? ToCertificate2(e.Certificate), certificateHost);
        };
        client.Config.RetryAttempts = 2;
        return (client, basePath);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default)
    {
        var (client, basePath) = CreateClient();
        await using var _ = client;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        var remoteDirectory = $"{basePath}/{relativeDirectory.Trim('/')}";
        if (!await client.DirectoryExists(remoteDirectory, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var items = await client.GetListing(remoteDirectory, cancellationToken).ConfigureAwait(false);
        return items.Where(item => item.Type == FtpObjectType.File).Select(item => item.Name).ToList();
    }

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default)
    {
        var (client, basePath) = CreateClient();
        await using var _ = client;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        var remoteDirectory = relativeDirectory.Trim('/').Length == 0 ? basePath : $"{basePath}/{relativeDirectory.Trim('/')}";
        if (!await client.DirectoryExists(remoteDirectory, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var items = await client.GetListing(remoteDirectory, cancellationToken).ConfigureAwait(false);
        return items.Where(item => item.Type == FtpObjectType.Directory).Select(item => item.Name).ToList();
    }

    public async Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default)
    {
        var clean = relativeDirectory.Trim('/');
        if (clean.Length == 0 || relativeDirectory.Contains(':') || clean.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"Refusing to delete '{relativeDirectory}'.", nameof(relativeDirectory));
        }

        var (client, basePath) = CreateClient();
        await using var _ = client;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        var remoteDirectory = $"{basePath}/{clean}";
        if (await client.DirectoryExists(remoteDirectory, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteDirectory(remoteDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var clean = relativePath.Trim('/');
        if (clean.Length == 0 || relativePath.Contains(':') || clean.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"Refusing to delete '{relativePath}'.", nameof(relativePath));
        }

        var (client, basePath) = CreateClient();
        await using var _ = client;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        var remotePath = $"{basePath}/{clean}";
        if (await client.FileExists(remotePath, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteFile(remotePath, cancellationToken).ConfigureAwait(false);
        }

        return !await client.FileExists(remotePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default)
    {
        var (client, basePath) = CreateClient();
        await using var _ = client;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        var remotePath = $"{basePath}/{relativePath.TrimStart('/')}";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        var status = await client.DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, token: cancellationToken).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
        {
            return false;
        }

        var remoteSize = await client.GetFileSize(remotePath, -1, cancellationToken).ConfigureAwait(false);
        return new FileInfo(localPath).Length == remoteSize;
    }

    public async Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var (client, basePath) = CreateClient();
            await using var _ = client;
            var remotePath = $"{basePath}/{relativePath.TrimStart('/')}";

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
