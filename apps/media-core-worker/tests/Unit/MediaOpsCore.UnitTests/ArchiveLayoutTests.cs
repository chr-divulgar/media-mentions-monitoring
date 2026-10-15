using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Where things go in an account: the raw backup under backup/<country>/<year>/<month>/<day>/<source>/..., and a note
// (its clip, or the PDF of a press note) under <country>/<client>/<year>/<month>/<day>/...
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
    public void NoteDirectory_is_country_client_and_day()
    {
        var day = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5));

        Assert.Equal("colombia/ecopetrol/2026/10/14", ArchiveLayout.NoteDirectory("Colombia", "Ecopetrol", day));
        Assert.Equal("colombia/ecopetrol-s-a/2026/10/14", ArchiveLayout.NoteDirectory(null, "Ecopetrol S.A.", day));
        Assert.Equal("colombia/sin-cliente/2026/10/14", ArchiveLayout.NoteDirectory("colombia", "  ", day));
    }
}
