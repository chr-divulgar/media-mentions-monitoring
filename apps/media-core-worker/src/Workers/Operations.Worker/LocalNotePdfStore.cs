using System.Globalization;
using MediaOpsCore.Modules.Alerting.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// The PDF of each web note, made when its alert is created (the page may change or disappear before anybody edits the
/// note) and kept on this machine for the current day and the one before; older days are deleted. Nothing goes to the
/// archive from here: the PDF is uploaded only when the note is saved (NotePdfService.CommitAsync), so an alert nobody
/// edits never uploads anything. Laid out as {root}/{yyyy-MM-dd}/{alertId}.pdf, the day being the Bogota day of the alert.
/// </summary>
public sealed class LocalNotePdfStore : IWebNotePdfPrefetcher
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private const string DayFormat = "yyyy-MM-dd";

    // A page takes seconds to print and a burst of alerts must not open a dozen browser pages at once.
    private readonly SemaphoreSlim printGate = new(2, 2);
    private readonly IBrowserPagePrinter printer;
    private readonly string root;
    private readonly ILogger<LocalNotePdfStore> logger;
    private readonly Func<DateTimeOffset> clock;

    public LocalNotePdfStore(IBrowserPagePrinter printer, string root, ILogger<LocalNotePdfStore> logger, Func<DateTimeOffset>? clock = null)
    {
        this.printer = printer;
        this.root = root;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Request(WebNotePdfRequest request) =>
        _ = Task.Run(() => GenerateAsync(request, CancellationToken.None));

    // Internal so a test can await it. Never throws: a page that cannot be printed is printed again when the note is opened.
    internal async Task GenerateAsync(WebNotePdfRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsSafeId(request.AlertId) || Find(request.AlertId) is not null)
            {
                return;
            }

            await printGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var day = DayOf(request.CapturedAt);
                var target = Path.Combine(root, day.ToString(DayFormat, CultureInfo.InvariantCulture), $"{request.AlertId}.pdf");
                var temporary = target + ".part";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (!await printer.PrintPdfAsync(request.Url, temporary, request.Words, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(temporary);
                    logger.LogInformation("[LocalNotePdfStore] Could not print {Url}; it will be printed when the note is opened.", request.Url);
                    return;
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                printGate.Release();
            }

            PurgeOld();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "[LocalNotePdfStore] Could not keep the PDF of alert {AlertId}.", request.AlertId);
        }
    }

    /// <summary>The stored PDF of an alert, when it is from the current day or the one before; null otherwise.</summary>
    public string? Find(string alertId)
    {
        if (!IsSafeId(alertId))
        {
            return null;
        }

        var today = DayOf(clock());
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            var path = Path.Combine(root, day.ToString(DayFormat, CultureInfo.InvariantCulture), $"{alertId}.pdf");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public void PurgeOld() => PurgeOld(DayOf(clock()));

    /// <summary>Deletes the day folders before yesterday. Only folders named like a day are touched.</summary>
    public void PurgeOld(DateOnly today)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            if (DateOnly.TryParseExact(Path.GetFileName(directory), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < today.AddDays(-1))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "[LocalNotePdfStore] Could not delete {Directory}.", directory);
                }
            }
        }
    }

    private static DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(moment.ToOffset(BogotaOffset).DateTime);

    // An alert id is a Mongo ObjectId (hex): anything that could be a path is refused.
    private static bool IsSafeId(string id) => id.Length is > 0 and <= 40 && id.All(char.IsAsciiLetterOrDigit);
}
