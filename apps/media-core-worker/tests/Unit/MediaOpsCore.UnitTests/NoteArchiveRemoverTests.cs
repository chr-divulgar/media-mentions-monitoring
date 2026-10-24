using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Deleting a note also deletes the file it has in the archive (the PDF of a web note, the clip of a radio or TV one).
// The archive keeps the backup of every recording too, so only a note's own file, named by its public link, may go.
public sealed class NoteArchiveRemoverTests
{
    private sealed class FakeStorage : IArchiveStorage
    {
        public List<string> Deleted { get; } = [];
        public bool Fails { get; init; }

        public Task<bool> DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            if (Fails) throw new IOException("server down");
            Deleted.Add(relativePath);
            return Task.FromResult(true);
        }

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static readonly Dictionary<string, string> Addresses = new()
    {
        [ArchiveMedia.Press] = "https://chrdivulgar.org/",
        [ArchiveMedia.Radio] = "https://chrmonitoreo.org",
        [ArchiveMedia.Television] = "https://chrmonitoreo.com",
    };

    private static NoteArchiveRemover Remover(FakeStorage press, FakeStorage radio, FakeStorage television) =>
        new(
            new ArchiveFixture.Storages(radio, television, press),
            medium => Addresses.GetValueOrDefault(medium),
            NullLogger<NoteArchiveRemover>.Instance);

    [Fact]
    public async Task Deletes_the_pdf_of_a_web_note_from_the_press_account_by_its_public_link()
    {
        var press = new FakeStorage();
        var remover = Remover(press, new FakeStorage(), new FakeStorage());

        var result = await remover.DeleteAsync(["https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-salida-de-rodrigo%20lara.pdf"]);

        Assert.Equal(["colombia/ecopetrol/2026/10/23/infobae-01-salida-de-rodrigo lara.pdf"], press.Deleted);
        Assert.Single(result.Deleted);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task Deletes_a_radio_or_tv_clip_from_its_own_medium_account()
    {
        var radio = new FakeStorage();
        var television = new FakeStorage();
        var remover = Remover(new FakeStorage(), radio, television);

        await remover.DeleteAsync([
            "https://chrmonitoreo.org/colombia/ecopetrol/2026/10/23/blu-radio_2026-10-23_10-30-48_6700ab12cd.mp3",
            "https://chrmonitoreo.com/colombia/ecopetrol/2026/10/23/rcn_2026-10-23_10-30-48_6700ab12cd.mp4",
        ]);

        Assert.Equal(["colombia/ecopetrol/2026/10/23/blu-radio_2026-10-23_10-30-48_6700ab12cd.mp3"], radio.Deleted);
        Assert.Equal(["colombia/ecopetrol/2026/10/23/rcn_2026-10-23_10-30-48_6700ab12cd.mp4"], television.Deleted);
    }

    [Theory]
    [InlineData("https://www.infobae.com/colombia/2026/10/23/nota/")] // the article itself, not ours
    [InlineData("https://chrdivulgar.org/backup/colombia/2026/10/23/rcn_tv/rcn_tv_2026-10-23_10-00-00.mp4")] // a recording of the backup
    [InlineData("https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/../../../../index.html")]
    [InlineData("https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/script.php")]
    [InlineData("https://chrdivulgar.org/colombia/ecopetrol/2026/10/23")] // a whole day
    [InlineData("")]
    public async Task Never_deletes_anything_that_is_not_a_notes_own_file(string link)
    {
        var press = new FakeStorage();
        var remover = Remover(press, new FakeStorage(), new FakeStorage());

        var result = await remover.DeleteAsync([link]);

        Assert.Empty(press.Deleted);
        Assert.Empty(result.Deleted);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task Reports_a_file_it_could_not_delete()
    {
        var remover = Remover(new FakeStorage { Fails = true }, new FakeStorage(), new FakeStorage());

        var result = await remover.DeleteAsync(["https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-x.pdf"]);

        Assert.Empty(result.Deleted);
        Assert.Equal(["https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-x.pdf"], result.Failed);
    }
}
