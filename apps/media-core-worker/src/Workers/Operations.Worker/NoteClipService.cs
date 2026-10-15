using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Body of POST /notes/clip: the clip a radio or TV note was made with, and what it is a note of.</summary>
public sealed class NoteClipRequest
{
    // "radio" or "tv" ("television" too): picks the archive account.
    public string Medium { get; set; } = string.Empty;

    public string Client { get; set; } = string.Empty;

    // The station or channel's name as the alert carries it; opens the file name, and says which source (and so
    // which country) the note is from.
    public string Platform { get; set; } = string.Empty;

    // Left out, the country of the platform's source in the catalog, else Colombia.
    public string? Country { get; set; }

    // The day of the note, yyyy-MM-dd: its folder.
    public string Date { get; set; } = string.Empty;

    // The moment of the clip, yyyy-MM-dd_HH-mm-ss: part of the file name.
    public string Stamp { get; set; } = string.Empty;

    public string AlertId { get; set; } = string.Empty;

    // The clip on this machine: fragment_{alertId}.mp3 or .mp4, which the API writes when the note is edited.
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>200 body of POST /notes/clip.</summary>
public sealed class NoteClipResponse
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>Where a clip was filed, or why it was not (Spanish: it can reach the user).</summary>
public sealed record NoteClipResult(string? Path, string? Error)
{
    public bool Succeeded => Path is not null;
}

/// <summary>
/// Files the clip of a radio or TV note in the archive, in the account of its medium, under
/// {country}/{client}/{yyyy}/{MM}/{dd}/ (see ArchiveLayout). Those are the notes — what the contract requires keeping —
/// so, unlike the backup of the raw recordings, nothing ever purges them. Saving the same note again overwrites its
/// clip in place: the name carries the alert's id.
/// </summary>
public sealed partial class NoteClipService
{
    private readonly IArchiveStorageProvider storages;
    private readonly ICaptureSourceRepository sources;
    private readonly ILogger<NoteClipService> logger;

    public NoteClipService(IArchiveStorageProvider storages, ICaptureSourceRepository sources, ILogger<NoteClipService> logger)
    {
        this.storages = storages;
        this.sources = sources;
        this.logger = logger;
    }

    public async Task<NoteClipResult> ArchiveAsync(NoteClipRequest request, CancellationToken cancellationToken = default)
    {
        var medium = request.Medium.Trim().ToLowerInvariant() switch
        {
            "radio" => ArchiveMedia.Radio,
            "tv" or "television" => ArchiveMedia.Television,
            _ => null,
        };
        if (medium is null)
        {
            return new NoteClipResult(null, "Solo las notas de radio y televisión tienen un clip para archivar.");
        }

        // Only what the API itself writes: a fragment_{id}.mp3 / .mp4. This endpoint reads a file by path, so it must not
        // be usable to upload whatever is on the disk.
        var fileName = Path.GetFileName(request.FilePath);
        if (!FragmentFile().IsMatch(fileName)
            || !DayPattern().IsMatch(request.Date) || !DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", out var day)
            || !StampPattern().IsMatch(request.Stamp) || !IdPattern().IsMatch(request.AlertId))
        {
            return new NoteClipResult(null, "La solicitud del clip no es válida.");
        }

        if (!File.Exists(request.FilePath))
        {
            return new NoteClipResult(null, "El clip de la nota ya no está en el disco.");
        }

        var storage = storages.For(medium);
        if (storage is null)
        {
            return new NoteClipResult(null, $"No hay una cuenta de archivo configurada para {medium}.");
        }

        var country = request.Country ?? await CountryOfAsync(request.Platform, cancellationToken).ConfigureAwait(false);
        var platform = NotePdfService.ToFileSlug(request.Platform, 40, "sin-fuente");
        var remote = $"{ArchiveLayout.NoteDirectory(country, request.Client, day)}/{platform}_{request.Stamp}_{request.AlertId}{Path.GetExtension(fileName).ToLowerInvariant()}";

        try
        {
            if (!await storage.UploadAsync(request.FilePath, remote, cancellationToken).ConfigureAwait(false))
            {
                return new NoteClipResult(null, "No se pudo subir el clip de la nota al servidor de archivo.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[NoteClipService] Could not archive the clip of alert {AlertId}.", request.AlertId);
            return new NoteClipResult(null, "No se pudo subir el clip de la nota al servidor de archivo.");
        }

        return new NoteClipResult(remote, null);
    }

    // The country of the station or channel the alert is from, by its display name in the catalog.
    private async Task<string?> CountryOfAsync(string platform, CancellationToken cancellationToken)
    {
        try
        {
            var all = await sources.ListAllAsync(cancellationToken).ConfigureAwait(false);
            return all.FirstOrDefault(source => string.Equals(source.Platform, platform, StringComparison.OrdinalIgnoreCase))?.Country;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[NoteClipService] Could not read the catalog for the country of {Platform}.", platform);
            return null;
        }
    }

    [GeneratedRegex(@"^fragment_[A-Za-z0-9]+\.(mp3|mp4)$", RegexOptions.IgnoreCase)]
    private static partial Regex FragmentFile();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$")]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,40}$")]
    private static partial Regex IdPattern();
}
