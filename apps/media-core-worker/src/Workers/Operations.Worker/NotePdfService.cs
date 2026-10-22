using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Body of POST /notes/pdf/preview.</summary>
public sealed class NotePdfPreviewRequest
{
    public string Title { get; set; } = string.Empty;

    // The outlet (El Tiempo, Forbes...): it opens the file name and numbers that outlet's notes of the day.
    public string Outlet { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    // The article's publish date: decides the archive's year/month/day folder.
    public DateTimeOffset Date { get; set; }

    // The client the note is for: its folder ({country}/{client}/{yyyy}/{MM}/{dd}). Left out, "sin-cliente".
    public string? Client { get; set; }

    // Left out, Colombia: an outlet found through the news search has no country of its own.
    public string? Country { get; set; }
}

/// <summary>200 body of POST /notes/pdf/preview.</summary>
public sealed class NotePdfPreviewResponse
{
    public string PreviewId { get; set; } = string.Empty;

    // Where the PDF will live once saved; nothing exists there yet.
    public string Link { get; set; } = string.Empty;
}

/// <summary>Body of POST /notes/pdf/commit.</summary>
public sealed class NotePdfCommitRequest
{
    public string PreviewId { get; set; } = string.Empty;
}

/// <summary>200 body of POST /notes/pdf/commit.</summary>
public sealed class NotePdfResponse
{
    public string Link { get; set; } = string.Empty;
}

/// <summary>A printed PDF waiting to be saved, or why there is none (Spanish: it is shown to the user).</summary>
public sealed record NotePdfPreviewResult(string? PreviewId, string? Link, string? Error)
{
    public bool Succeeded => PreviewId is not null;
}

/// <summary>Link to the archived PDF, or why there is none (Spanish: it is shown to the user).</summary>
public sealed record NotePdfResult(string? Link, string? Error)
{
    public bool Succeeded => Link is not null;
}

/// <summary>
/// The permanent PDF copy of a web note (RQ-014), in two steps so nothing reaches the archive until
/// the user saves: PreviewAsync prints the article's page with the headless browser into a scratch
/// folder and says which public link the file will get; CommitAsync, on save, uploads it there.
///
/// The file is named {outlet}-{NN}-{title}.pdf inside the day's folder: NN counts that outlet's
/// notes of the day (01, 02...), so no two notes ever share a file. A note that already has its
/// file keeps its number and overwrites it in place, so saving again is idempotent.
/// </summary>
public sealed partial class NotePdfService
{
    // A PDF the user made by hand and uploads (a page the browser could not print): bigger than this is not a note.
    public const int MaxUploadBytes = 25 * 1024 * 1024;

    private const int MaxSlugLength = 120;
    private const int MaxOutletSlugLength = 40;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(2);

    private readonly IBrowserPagePrinter printer;
    private readonly IArchiveStorage storage;
    private readonly string publicBaseUrl;
    private readonly string scratchDirectory;
    private readonly ILogger<NotePdfService> logger;
    // Picking the next number and uploading must not interleave, or two notes of one outlet could
    // both take the same NN.
    private readonly SemaphoreSlim numberingGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingPreview> previews = new();

    public NotePdfService(
        IBrowserPagePrinter printer,
        IArchiveStorage storage,
        string publicBaseUrl,
        string scratchDirectory,
        ILogger<NotePdfService> logger)
    {
        this.printer = printer;
        this.storage = storage;
        this.publicBaseUrl = publicBaseUrl;
        this.scratchDirectory = scratchDirectory;
        this.logger = logger;
    }

    private sealed record PendingPreview(
        string LocalPath, string OutletSlug, string TitleSlug, DateTimeOffset Date, string? Client, string? Country, DateTimeOffset CreatedAt);

    public async Task<NotePdfPreviewResult> PreviewAsync(
        string outlet, string title, string url, DateTimeOffset date, string? client = null, string? country = null,
        CancellationToken cancellationToken = default)
    {
        PurgeExpired();

        var previewId = Guid.NewGuid().ToString("N");
        var localPath = Path.Combine(scratchDirectory, $"{previewId}.pdf");

        if (!await printer.PrintPdfAsync(url, localPath, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(localPath);
            return new NotePdfPreviewResult(null, null, "No se pudo generar el PDF de la página (no cargó o está bloqueada).");
        }

        return await RegisterPreviewAsync(previewId, localPath, outlet, title, date, client, country, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same preview, from a PDF the user made themselves (the page could not be printed: it blocks the browser or
    /// cannot be verified). The file takes the place of the printed one; from here on the flow is the same, so nothing
    /// reaches the archive until the note is saved, and it is named and numbered the same way.
    /// </summary>
    public async Task<NotePdfPreviewResult> PreviewFromFileAsync(
        byte[] pdf, string outlet, string title, DateTimeOffset date, string? client = null, string? country = null,
        CancellationToken cancellationToken = default)
    {
        PurgeExpired();

        if (pdf.Length > MaxUploadBytes)
        {
            return new NotePdfPreviewResult(null, null, "El PDF pesa más de 25 MB.");
        }

        if (!pdf.AsSpan().StartsWith("%PDF-"u8))
        {
            return new NotePdfPreviewResult(null, null, "El archivo no es un PDF.");
        }

        var previewId = Guid.NewGuid().ToString("N");
        var localPath = Path.Combine(scratchDirectory, $"{previewId}.pdf");
        Directory.CreateDirectory(scratchDirectory);
        await File.WriteAllBytesAsync(localPath, pdf, cancellationToken).ConfigureAwait(false);

        return await RegisterPreviewAsync(previewId, localPath, outlet, title, date, client, country, cancellationToken).ConfigureAwait(false);
    }

    // Names the file in the archive and remembers the scratch PDF until the note is saved (or it expires).
    private async Task<NotePdfPreviewResult> RegisterPreviewAsync(
        string previewId, string localPath, string outlet, string title, DateTimeOffset date, string? client, string? country,
        CancellationToken cancellationToken)
    {
        var outletSlug = ToFileSlug(outlet, MaxOutletSlugLength, "sin-fuente");
        var titleSlug = ToFileSlug(title);

        var target = await ChooseTargetAsync(outletSlug, titleSlug, date, client, country, cancellationToken).ConfigureAwait(false);
        if (target.Error is not null)
        {
            TryDelete(localPath);
            return new NotePdfPreviewResult(null, null, target.Error);
        }

        previews[previewId] = new PendingPreview(localPath, outletSlug, titleSlug, date, client, country, DateTimeOffset.UtcNow);
        return new NotePdfPreviewResult(previewId, BuildPublicLink(publicBaseUrl, target.RelativePath!), null);
    }

    // The printed file for the viewer, or null when the preview expired or never existed.
    public string? FindPreviewFile(string previewId) =>
        previews.TryGetValue(previewId, out var preview) && File.Exists(preview.LocalPath) ? preview.LocalPath : null;

    // Uploads a previewed PDF. The name is chosen again here (the folder may have changed since the
    // preview), so the link returned is the final one.
    public async Task<NotePdfResult> CommitAsync(string previewId, CancellationToken cancellationToken = default)
    {
        if (!previews.TryGetValue(previewId, out var preview) || !File.Exists(preview.LocalPath))
        {
            return new NotePdfResult(null, "La vista previa del PDF expiró. Cierra y vuelve a abrir la nota.");
        }

        await numberingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = await ChooseTargetCoreAsync(
                preview.OutletSlug, preview.TitleSlug, preview.Date, preview.Client, preview.Country, cancellationToken).ConfigureAwait(false);
            if (target.Error is not null)
            {
                return new NotePdfResult(null, target.Error);
            }

            if (!await storage.UploadAsync(preview.LocalPath, target.RelativePath!, cancellationToken).ConfigureAwait(false))
            {
                // The preview stays, so saving can simply be retried.
                return new NotePdfResult(null, "No se pudo subir el PDF al servidor de archivo.");
            }

            previews.TryRemove(previewId, out _);
            TryDelete(preview.LocalPath);
            return new NotePdfResult(BuildPublicLink(publicBaseUrl, target.RelativePath!), null);
        }
        finally
        {
            numberingGate.Release();
        }
    }

    private async Task<(string? RelativePath, string? Error)> ChooseTargetAsync(
        string outletSlug, string titleSlug, DateTimeOffset date, string? client, string? country, CancellationToken cancellationToken)
    {
        await numberingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ChooseTargetCoreAsync(outletSlug, titleSlug, date, client, country, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            numberingGate.Release();
        }
    }

    // Caller holds numberingGate.
    private async Task<(string? RelativePath, string? Error)> ChooseTargetCoreAsync(
        string outletSlug, string titleSlug, DateTimeOffset date, string? client, string? country, CancellationToken cancellationToken)
    {
        var directory = BuildDirectory(country, client, date);
        try
        {
            var existing = await storage.ListAsync(directory, cancellationToken).ConfigureAwait(false);
            return ($"{directory}/{ChooseFileName(outletSlug, titleSlug, existing)}", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[NotePdfService] Could not list {Directory} to number the PDF.", directory);
            return (null, "No se pudo consultar el servidor de archivo para numerar el PDF.");
        }
    }

    // Previews nobody saved pile up in the scratch folder (and survive restarts): drop the old ones.
    private void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - PreviewLifetime;
        foreach (var (id, preview) in previews)
        {
            if (preview.CreatedAt < cutoff && previews.TryRemove(id, out _))
            {
                TryDelete(preview.LocalPath);
            }
        }

        if (!Directory.Exists(scratchDirectory))
        {
            return;
        }

        foreach (var file in new DirectoryInfo(scratchDirectory).EnumerateFiles("*.pdf"))
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                TryDelete(file.FullName);
            }
        }
    }

    // Numbers are always two digits or more, which keeps "canal-1-01-x.pdf" (outlet "canal-1") from being
    // read as outlet "canal". An outlet that itself ends in a 2+ digit number can still be misread;
    // ponytail: put a separator that slugs never contain in the name if that ever happens.
    // {outlet}-{NN}-{title}.pdf. The note's own file (same outlet and title) is reused as is;
    // otherwise it takes the outlet's next number of the day.
    internal static string ChooseFileName(string outletSlug, string titleSlug, IEnumerable<string> existingNames)
    {
        var pattern = new Regex($@"^{Regex.Escape(outletSlug)}-(\d{{2,}})-(.+)\.pdf$", RegexOptions.IgnoreCase);
        var highest = 0;
        foreach (var name in existingNames)
        {
            if (pattern.Match(name) is not { Success: true } match)
            {
                continue;
            }

            if (match.Groups[2].Value.Equals(titleSlug, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            highest = Math.Max(highest, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        return $"{outletSlug}-{(highest + 1).ToString("D2", CultureInfo.InvariantCulture)}-{titleSlug}.pdf";
    }

    // Lowercase, accents stripped, spaces (and any other run of non-alphanumerics) as a single '-'.
    // Only [a-z0-9-] survives, so the title can never escape the folder or break the https link.
    internal static string ToFileSlug(string title, int maxLength = MaxSlugLength, string fallback = "nota")
    {
        var decomposed = (title ?? string.Empty).Normalize(NormalizationForm.FormD);
        var withoutAccents = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutAccents.Append(character);
            }
        }

        var slug = NonAlphanumericRun().Replace(withoutAccents.ToString().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? fallback : slug;
    }

    // The notes' folder: {country}/{client}/{yyyy}/{MM}/{dd}, the same shape as the clips of radio and TV notes.
    internal static string BuildDirectory(string? country, string? client, DateTimeOffset date) =>
        ArchiveLayout.NoteDirectory(country, client, date);

    internal static string BuildPublicLink(string baseUrl, string relativePath) =>
        $"{baseUrl.TrimEnd('/')}/{string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString))}";

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "[NotePdfService] Could not delete scratch file {Path}.", path);
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRun();
}
