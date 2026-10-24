using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Body of POST /notes/archive/delete: the links a deleted note had (its PDF, its clip, the article...).</summary>
public sealed class NoteArchiveDeleteRequest
{
    public List<string> Links { get; set; } = [];
}

/// <summary>What was deleted from the archive (paths) and which links could not be (to retry).</summary>
public sealed record NoteArchiveDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// Deletes from the archive the file a deleted note had there: the PDF of a web note (press account) or the clip of a
/// radio or TV note (that medium's account). The file is found by its public link: a link under one of the media's
/// public addresses names the path in that medium's account. Only a note's own file is ever deleted — a path shaped
/// {country}/{client}/{yyyy}/{MM}/{dd}/{file}.pdf|.mp3|.mp4, never anything of the backup — and any other link (the
/// article itself) is left alone.
/// </summary>
public sealed partial class NoteArchiveRemover
{
    private static readonly string[] Media = [ArchiveMedia.Press, ArchiveMedia.Radio, ArchiveMedia.Television];

    private readonly IArchiveStorageProvider storages;
    private readonly Func<string, string?> publicBaseUrlFor;
    private readonly ILogger<NoteArchiveRemover> logger;

    public NoteArchiveRemover(IArchiveStorageProvider storages, Func<string, string?> publicBaseUrlFor, ILogger<NoteArchiveRemover> logger)
    {
        this.storages = storages;
        this.publicBaseUrlFor = publicBaseUrlFor;
        this.logger = logger;
    }

    public async Task<NoteArchiveDeleteResult> DeleteAsync(IEnumerable<string> links, CancellationToken cancellationToken = default)
    {
        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var link in links.Where(link => !string.IsNullOrWhiteSpace(link)).Distinct())
        {
            if (Locate(link.Trim()) is not { } target)
            {
                continue;
            }

            var storage = storages.For(target.Medium);
            if (storage is null)
            {
                failed.Add(link);
                continue;
            }

            try
            {
                if (await storage.DeleteFileAsync(target.Path, cancellationToken).ConfigureAwait(false))
                {
                    deleted.Add(target.Path);
                    logger.LogInformation("[NoteArchiveRemover] Deleted {Path} from the {Medium} archive (its note was deleted).", target.Path, target.Medium);
                }
                else
                {
                    failed.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[NoteArchiveRemover] Could not delete {Path} from the {Medium} archive.", target.Path, target.Medium);
                failed.Add(link);
            }
        }

        return new NoteArchiveDeleteResult(deleted, failed);
    }

    // The medium account and path a public link names; null when it is not a note's own file in the archive.
    private (string Medium, string Path)? Locate(string link)
    {
        foreach (var medium in Media)
        {
            var baseUrl = publicBaseUrlFor(medium)?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl) || !link.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = string.Join('/', link[(baseUrl.Length + 1)..].Split('/').Select(Uri.UnescapeDataString));
            return NoteFilePath().IsMatch(path) && !path.Split('/').Any(segment => segment is "." or "..")
                ? (medium, path)
                : null;
        }

        return null;
    }

    // {country}/{client}/{yyyy}/{MM}/{dd}/{file}.pdf|.mp3|.mp4 — what ArchiveLayout.NoteDirectory files the notes under.
    [GeneratedRegex(@"^(?!backup/)[a-z0-9-]{1,30}/[a-z0-9-]{1,60}/\d{4}/\d{2}/\d{2}/[^/\\]+\.(pdf|mp3|mp4)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteFilePath();
}
