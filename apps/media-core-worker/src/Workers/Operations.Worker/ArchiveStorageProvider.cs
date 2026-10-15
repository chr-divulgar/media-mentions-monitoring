using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// One FTP/FTPS storage per medium, each with that medium's own account (OperationsWorkerOptions.CredentialsFor): a
/// medium without an account of its own falls back to the single one used before, and with none gets nothing. Two
/// media that share an account share one connection setup, but never more than the folders they own.
/// </summary>
public sealed class ArchiveStorageProvider : IArchiveStorageProvider
{
    private static readonly string[] Media = [ArchiveMedia.Radio, ArchiveMedia.Television, ArchiveMedia.Press];

    private readonly Dictionary<string, IArchiveStorage> storages = new(StringComparer.Ordinal);

    public ArchiveStorageProvider(OperationsWorkerOptions options, ILoggerFactory loggerFactory)
    {
        if (!options.ArchiveEnabled)
        {
            return;
        }

        // The same account for several media is one storage: no need for a connection setup per medium.
        var byAccount = new Dictionary<FtpArchiveCredentials, IArchiveStorage>();
        foreach (var medium in Media)
        {
            if (options.CredentialsFor(medium) is not { } credentials)
            {
                continue;
            }

            if (!byAccount.TryGetValue(credentials, out var storage))
            {
                storage = new FtpArchiveStorage(credentials, options, loggerFactory.CreateLogger<FtpArchiveStorage>());
                byAccount[credentials] = storage;
            }

            storages[medium] = storage;
        }
    }

    public IArchiveStorage? For(string medium) => storages.GetValueOrDefault(medium);
}
