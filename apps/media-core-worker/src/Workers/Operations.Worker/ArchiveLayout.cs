namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Where things go inside an archive account. Two kinds of content, two shapes:
///
///   the raw backup of the recordings   backup/{country}/{yyyy}/{MM}/{dd}/{source}/{file}
///   a radio or TV note (its clip)      {country}/{client}/{yyyy}/{MM}/{dd}/{file}
///   a web note (its PDF)               {yyyy}/{MONTH}/{dd}/{file}, the day of the 6 a.m. report
///
/// The backup is the company's own copy of what was recorded and is deleted after a few days (see
/// ArchiveRetentionWorker); the notes are what the contract requires keeping, so they live outside the backup
/// folder and are never purged. The medium is not part of the path: each medium has its own account.
/// </summary>
internal static class ArchiveLayout
{
    public const string BackupRoot = "backup";

    // The country of anything whose country is not known (the catalog leaves it empty for most sources).
    public const string DefaultCountry = "colombia";

    private const int MaxCountryLength = 30;
    private const int MaxClientLength = 60;

    // Lowercase, no accents, '-' between words: "México" is "mexico". Only [a-z0-9-] survives, so a name can never
    // escape its folder.
    public static string CountrySlug(string? country) =>
        NotePdfService.ToFileSlug(country ?? string.Empty, MaxCountryLength, DefaultCountry);

    public static string ClientSlug(string? client) =>
        NotePdfService.ToFileSlug(client ?? string.Empty, MaxClientLength, "sin-cliente");

    // The local file {medium}/{yyyy}/{MM}/{dd}/{source}/{file} as the backup keeps it: the medium folder gives way to
    // backup/{country}.
    public static string BackupPathFor(string? country, string localRelativePath)
    {
        var withoutMedium = localRelativePath.Split('/', 2)[1];
        return $"{BackupRoot}/{CountrySlug(country)}/{withoutMedium}";
    }

    // The folder holding one source's files of one day.
    public static string BackupDirectory(string? country, DateOnly day, string sourceId) =>
        $"{BackupRoot}/{CountrySlug(country)}/{day.Year:D4}/{day.Month:D2}/{day.Day:D2}/{sourceId}";

    // The folder of the notes of one client on one day.
    public static string NoteDirectory(string? country, string? client, DateOnly day) =>
        $"{CountrySlug(country)}/{ClientSlug(client)}/{day.Year:D4}/{day.Month:D2}/{day.Day:D2}";

    public static string NoteDirectory(string? country, string? client, DateTimeOffset date) =>
        NoteDirectory(country, client, new DateOnly(date.Year, date.Month, date.Day));

    private static readonly string[] MonthNames =
        ["ENERO", "FEBRERO", "MARZO", "ABRIL", "MAYO", "JUNIO", "JULIO", "AGOSTO", "SEPTIEMBRE", "OCTUBRE", "NOVIEMBRE", "DICIEMBRE"];

    // The folder of a web note (its PDF): {yyyy}/{MONTH}/{dd} of the report it goes in. The report is cut at 6 a.m.
    // Bogota, so a note from 6:00 of one day to 5:59 of the next is filed under that next day.
    public static string WebNoteDirectory(DateTimeOffset moment)
    {
        var day = DateOnly.FromDateTime(moment.ToOffset(TimeSpan.FromHours(-5)).AddHours(18).DateTime);
        return $"{day.Year:D4}/{MonthNames[day.Month - 1]}/{day.Day:D2}";
    }
}
