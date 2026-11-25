using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Where things go in an account: the raw backup under backup/<country>/<year>/<month>/<day>/<source>/..., radio notes
// under <client>/<year>/<month>/<day>/..., TV notes under <client>/<Month_yyyy>/<day>/..., and web notes under
// <year>/<MONTH>/<day>/...
public sealed class ArchiveLayoutTests
{
    [Theory]
    [InlineData("Colombia", "colombia")]
    [InlineData("colombia", "colombia")]
    [InlineData("México", "mexico")]
    [InlineData("  ", "colombia")]
    [InlineData(null, "colombia")]
    public void CountrySlug_is_lowercase_without_accents_and_defaults_to_colombia(string? country, string expected) =>
        Assert.Equal(expected, ArchiveLayout.CountrySlug(country));

    [Fact]
    public void BackupPathFor_replaces_the_medium_folder_with_backup_and_the_country()
    {
        Assert.Equal(
            "backup/colombia/2026/10/14/Blu_Radio/Blu_Radio_2026-10-14_19-00-00.opus",
            ArchiveLayout.BackupPathFor("Colombia", "radio/2026/10/14/Blu_Radio/Blu_Radio_2026-10-14_19-00-00.opus"));
        Assert.Equal(
            "backup/mexico/2026/10/14/azteca/azteca_2026-10-14_14-00-03.mp4",
            ArchiveLayout.BackupPathFor("México", "television/2026/10/14/azteca/azteca_2026-10-14_14-00-03.mp4"));
    }

    [Fact]
    public void BackupDirectory_is_where_the_files_of_one_source_and_day_are()
    {
        Assert.Equal("backup/colombia/2026/10/04/rcn_tv", ArchiveLayout.BackupDirectory("colombia", new DateOnly(2026, 10, 4), "rcn_tv"));
    }

    [Fact]
    public void RadioNoteDirectory_is_client_and_day()
    {
        var day = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5));

        Assert.Equal("ECOPETROL/2026/10/14", ArchiveLayout.RadioNoteDirectory("Ecopetrol", day));
        Assert.Equal("ECOPETROL-S-A/2026/10/14", ArchiveLayout.RadioNoteDirectory("Ecopetrol S.A.", day));
        Assert.Equal("SIN-CLIENTE/2026/10/14", ArchiveLayout.RadioNoteDirectory("  ", day));
        Assert.Equal("ECOPETROL/2026/10/14", ArchiveLayout.RadioNoteDirectory("Ecopetrol", new DateOnly(2026, 10, 14)));
    }

    [Fact]
    public void TelevisionNoteDirectory_is_client_month_year_and_day()
    {
        var day = new DateTimeOffset(2026, 11, 23, 21, 15, 0, TimeSpan.FromHours(-5));

        Assert.Equal("ECOPETROL/NOVIEMBRE_2026/23", ArchiveLayout.TelevisionNoteDirectory("Ecopetrol", day));
        Assert.Equal("SIN-CLIENTE/NOVIEMBRE_2026/23", ArchiveLayout.TelevisionNoteDirectory(null, day));
        Assert.Equal("ECOPETROL/NOVIEMBRE_2026/23", ArchiveLayout.TelevisionNoteDirectory("Ecopetrol", new DateOnly(2026, 11, 23)));
    }

    // The report is cut at 6 a.m. Bogota: a web note belongs to the day of the report it goes in.
    [Theory]
    [InlineData("2026-10-27T06:00:00-05:00", "2026/OCTUBRE/28")] // the cut itself opens the next report
    [InlineData("2026-10-27T23:30:00-05:00", "2026/OCTUBRE/28")]
    [InlineData("2026-10-28T05:59:00-05:00", "2026/OCTUBRE/28")]
    [InlineData("2026-10-28T06:00:00-05:00", "2026/OCTUBRE/29")]
    [InlineData("2026-10-28T10:00:00Z", "2026/OCTUBRE/28")] // 05:00 in Bogota
    [InlineData("2026-12-31T08:00:00-05:00", "2027/ENERO/01")]
    [InlineData("2026-09-15T12:00:00-05:00", "2026/SEPTIEMBRE/16")]
    public void WebNoteDirectory_is_the_report_day_with_the_month_in_capitals(string moment, string expected) =>
        Assert.Equal(expected, ArchiveLayout.WebNoteDirectory(DateTimeOffset.Parse(moment, System.Globalization.CultureInfo.InvariantCulture)));
}
