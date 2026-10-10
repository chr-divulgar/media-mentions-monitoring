using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class NotePdfServiceTests
{
    private static readonly DateTimeOffset Date = new(2026, 10, 6, 11, 41, 0, TimeSpan.FromHours(-5));
    private const string Folder = "notes/internet/2026/10/06";

    [Theory]
    [InlineData("Ecopetrol culmina su campaña exploratoria", "ecopetrol-culmina-su-campana-exploratoria")]
    [InlineData("  Consejo de Estado:  \"golpea\" a comunidad / bloqueos?  ", "consejo-de-estado-golpea-a-comunidad-bloqueos")]
    [InlineData("¡Mano de hierro! Gobierno De La Espriella", "mano-de-hierro-gobierno-de-la-espriella")]
    [InlineData("", "nota")]
    [InlineData("???", "nota")]
    public void ToFileSlug_lowercases_and_joins_words_with_dashes(string title, string expected)
    {
        Assert.Equal(expected, NotePdfService.ToFileSlug(title));
    }

    [Fact]
    public void ToFileSlug_caps_the_length_without_a_trailing_dash()
    {
        var slug = NotePdfService.ToFileSlug(string.Join(' ', Enumerable.Repeat("palabra", 40)));

        Assert.True(slug.Length <= 120);
        Assert.False(slug.EndsWith('-'));
    }

    [Theory]
    [InlineData("El Tiempo", "el-tiempo")]
    [InlineData("Forbes Colombia", "forbes-colombia")]
    [InlineData("", "sin-fuente")]
    public void ToFileSlug_of_an_outlet_falls_back_when_empty(string outlet, string expected)
    {
        Assert.Equal(expected, NotePdfService.ToFileSlug(outlet, 40, "sin-fuente"));
    }

    [Fact]
    public void ChooseFileName_starts_each_outlet_at_01_and_counts_up_per_outlet()
    {
        string[] existing = ["forbes-01-primera.pdf", "forbes-02-segunda.pdf", "el-tiempo-01-otra.pdf"];

        Assert.Equal("forbes-01-hola.pdf", NotePdfService.ChooseFileName("forbes", "hola", []));
        Assert.Equal("forbes-03-nueva.pdf", NotePdfService.ChooseFileName("forbes", "nueva", existing));
        Assert.Equal("el-tiempo-02-nueva.pdf", NotePdfService.ChooseFileName("el-tiempo", "nueva", existing));
        Assert.Equal("la-republica-01-nueva.pdf", NotePdfService.ChooseFileName("la-republica", "nueva", existing));
    }

    [Fact]
    public void ChooseFileName_keeps_the_number_of_a_note_that_already_has_its_file()
    {
        string[] existing = ["forbes-01-primera.pdf", "forbes-02-segunda.pdf"];

        Assert.Equal("forbes-01-primera.pdf", NotePdfService.ChooseFileName("forbes", "primera", existing));
    }

    [Fact]
    public void ChooseFileName_does_not_mix_up_outlets_that_share_a_prefix()
    {
        // "canal" must not read "canal-1-01-x.pdf" as its own note number 1 titled "x".
        Assert.Equal("canal-01-nota.pdf", NotePdfService.ChooseFileName("canal", "nota", ["canal-1-01-x.pdf"]));
    }

    [Fact]
    public void BuildPublicLink_joins_the_base_url_and_escapes_each_segment()
    {
        Assert.Equal(
            "https://server2.chrdivulgar.org/notas/notes/internet/2026/10/06/forbes-01-ecopetrol-culmina.pdf",
            NotePdfService.BuildPublicLink("https://server2.chrdivulgar.org/notas/", $"{NotePdfService.BuildDirectory(Date)}/forbes-01-ecopetrol-culmina.pdf"));
    }

    [Fact]
    public async Task Preview_prints_and_shows_the_link_but_uploads_nothing()
    {
        var printer = new FakePrinter();
        var storage = new FakeStorage { Existing = ["forbes-01-otra-nota.pdf"] };

        var preview = await Build(printer, storage).PreviewAsync("Forbes", "Ecopetrol culmina su campaña", "https://site.co/a", Date);

        Assert.True(preview.Succeeded);
        Assert.Equal("https://files.test/notes/internet/2026/10/06/forbes-02-ecopetrol-culmina-su-campana.pdf", preview.Link);
        Assert.Equal("https://site.co/a", printer.PrintedUrl);
        Assert.Null(storage.UploadedTo);
    }

    [Fact]
    public async Task Preview_keeps_the_printed_file_available_for_the_viewer()
    {
        var sut = Build(new FakePrinter(), new FakeStorage());

        var preview = await sut.PreviewAsync("Forbes", "Titular", "https://site.co/a", Date);

        var path = sut.FindPreviewFile(preview.PreviewId!);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Null(sut.FindPreviewFile("no-such-preview"));
    }

    [Fact]
    public async Task Commit_uploads_to_the_previewed_name_and_removes_the_scratch_file()
    {
        var storage = new FakeStorage();
        var sut = Build(new FakePrinter(), storage);
        var preview = await sut.PreviewAsync("Forbes", "Ecopetrol culmina", "https://site.co/a", Date);
        var scratch = sut.FindPreviewFile(preview.PreviewId!)!;

        var result = await sut.CommitAsync(preview.PreviewId!);

        Assert.True(result.Succeeded);
        Assert.Equal(preview.Link, result.Link);
        Assert.Equal($"{Folder}/forbes-01-ecopetrol-culmina.pdf", storage.UploadedTo);
        Assert.False(File.Exists(scratch));
        Assert.Null(sut.FindPreviewFile(preview.PreviewId!));
    }

    [Fact]
    public async Task Commit_numbers_again_so_two_notes_previewed_together_do_not_collide()
    {
        var storage = new FakeStorage();
        var sut = Build(new FakePrinter(), storage);
        var first = await sut.PreviewAsync("Forbes", "Primera", "https://site.co/a", Date);
        var second = await sut.PreviewAsync("Forbes", "Segunda", "https://site.co/b", Date);
        Assert.Contains("forbes-01-", first.Link);
        Assert.Contains("forbes-01-", second.Link);

        var savedFirst = await sut.CommitAsync(first.PreviewId!);
        var savedSecond = await sut.CommitAsync(second.PreviewId!);

        Assert.Contains("forbes-01-primera.pdf", savedFirst.Link);
        Assert.Contains("forbes-02-segunda.pdf", savedSecond.Link);
    }

    [Fact]
    public async Task Commit_saving_the_same_note_again_overwrites_its_own_file()
    {
        var storage = new FakeStorage();
        var sut = Build(new FakePrinter(), storage);

        var first = await sut.CommitAsync((await sut.PreviewAsync("Forbes", "Mismo titular", "https://site.co/a", Date)).PreviewId!);
        var again = await sut.CommitAsync((await sut.PreviewAsync("Forbes", "Mismo titular", "https://site.co/a", Date)).PreviewId!);

        Assert.Equal(first.Link, again.Link);
    }

    [Fact]
    public async Task Commit_keeps_the_preview_when_the_upload_fails_so_saving_can_be_retried()
    {
        var storage = new FakeStorage { Succeeds = false };
        var sut = Build(new FakePrinter(), storage);
        var preview = await sut.PreviewAsync("Forbes", "T", "https://site.co/a", Date);

        var result = await sut.CommitAsync(preview.PreviewId!);

        Assert.False(result.Succeeded);
        Assert.Contains("servidor", result.Error);
        Assert.NotNull(sut.FindPreviewFile(preview.PreviewId!));
    }

    [Fact]
    public async Task Commit_of_an_unknown_preview_says_it_expired()
    {
        var result = await Build(new FakePrinter(), new FakeStorage()).CommitAsync("gone");

        Assert.False(result.Succeeded);
        Assert.Contains("expiró", result.Error);
    }

    [Fact]
    public async Task Preview_reports_a_print_failure_and_keeps_nothing()
    {
        var sut = Build(new FakePrinter { Succeeds = false }, new FakeStorage());

        var preview = await sut.PreviewAsync("Forbes", "T", "https://site.co/a", Date);

        Assert.False(preview.Succeeded);
        Assert.Contains("PDF", preview.Error);
    }

    [Fact]
    public async Task Preview_fails_when_the_archive_cannot_be_listed()
    {
        var sut = Build(new FakePrinter(), new FakeStorage { ListFails = true });

        var preview = await sut.PreviewAsync("Forbes", "T", "https://site.co/a", Date);

        Assert.False(preview.Succeeded);
        Assert.Contains("numerar", preview.Error);
    }

    private static NotePdfService Build(FakePrinter printer, FakeStorage storage) =>
        new(printer, storage, "https://files.test", Path.Combine(Path.GetTempPath(), "note-pdf-tests"), NullLogger<NotePdfService>.Instance);

    private sealed class FakePrinter : IBrowserPagePrinter
    {
        public bool Succeeds { get; init; } = true;
        public string? PrintedUrl { get; private set; }
        public string LastPath { get; private set; } = string.Empty;

        public Task<bool> PrintPdfAsync(string url, string outputPath, CancellationToken cancellationToken = default)
        {
            PrintedUrl = url;
            LastPath = outputPath;
            if (Succeeds)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "%PDF-fake");
            }

            return Task.FromResult(Succeeds);
        }
    }

    // Behaves like the archive folder: what was uploaded shows up in the next listing.
    private sealed class FakeStorage : IArchiveStorage
    {
        public bool Succeeds { get; init; } = true;
        public bool ListFails { get; init; }
        public List<string> Existing { get; init; } = [];
        public string? UploadedTo { get; private set; }

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default)
        {
            if (Succeeds)
            {
                UploadedTo = relativePath;
                var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
                if (!Existing.Contains(name))
                {
                    Existing.Add(name);
                }
            }

            return Task.FromResult(Succeeds);
        }

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            ListFails ? throw new IOException("boom") : Task.FromResult<IReadOnlyList<string>>(Existing.ToArray());

        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
