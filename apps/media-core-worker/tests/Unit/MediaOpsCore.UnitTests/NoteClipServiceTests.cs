using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The clip of a radio or TV note goes to the account of its medium under {country}/{client}/{year}/{month}/{day}/.
public sealed class NoteClipServiceTests
{
    private sealed class FakeStorage : IArchiveStorage
    {
        public bool Succeeds { get; set; } = true;
        public List<string> Uploaded { get; } = [];

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default)
        {
            Uploaded.Add(relativePath);
            return Task.FromResult(Succeeds);
        }

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeSources(params CaptureSource[] sources) : ICaptureSourceRepository
    {
        public Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaptureSource>>(sources);
        public Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> UpdateFallbackUrlsAsync(string sourceId, IReadOnlyList<string> fallbackStreamUrls, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static NoteClipService Service(FakeStorage? radio, FakeStorage? television, params CaptureSource[] sources) =>
        new(new ArchiveFixture.Storages(radio, television), new FakeSources(sources), NullLogger<NoteClipService>.Instance);

    private static string MakeClip(string name = "fragment_6700ab12cd.mp3")
    {
        var directory = Path.Combine(Path.GetTempPath(), $"noteclip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "clip");
        return path;
    }

    private static NoteClipRequest Request(string filePath, string medium = "radio", string platform = "Blu Radio") => new()
    {
        Medium = medium, Client = "Ecopetrol", Platform = platform, Date = "2026-10-14", Stamp = "2026-10-14_10-30-48",
        AlertId = "6700ab12cd", FilePath = filePath,
    };

    [Fact]
    public async Task ArchiveAsync_files_a_radio_clip_in_the_radio_account_under_country_client_and_day()
    {
        var radio = new FakeStorage();
        var television = new FakeStorage();
        var clip = MakeClip();

        var result = await Service(radio, television).ArchiveAsync(Request(clip));

        Assert.Null(result.Error);
        Assert.Equal("colombia/ecopetrol/2026/10/14/blu-radio_2026-10-14_10-30-48_6700ab12cd.mp3", result.Path);
        Assert.Equal([result.Path!], radio.Uploaded);
        Assert.Empty(television.Uploaded);
    }

    [Fact]
    public async Task ArchiveAsync_files_a_tv_clip_in_the_television_account()
    {
        var radio = new FakeStorage();
        var television = new FakeStorage();
        var clip = MakeClip("fragment_6700ab12cd.mp4");

        var result = await Service(radio, television).ArchiveAsync(Request(clip, medium: "tv", platform: "RCN"));

        Assert.Equal("colombia/ecopetrol/2026/10/14/rcn_2026-10-14_10-30-48_6700ab12cd.mp4", result.Path);
        Assert.Equal([result.Path!], television.Uploaded);
        Assert.Empty(radio.Uploaded);
    }

    [Fact]
    public async Task ArchiveAsync_uses_the_country_of_the_source_the_note_came_from()
    {
        var radio = new FakeStorage();
        var mexico = new CaptureSource("azteca_radio", "global-ingestion", "Azteca Radio", "radio", "https://x/stream", country: "México");

        var result = await Service(radio, null, mexico).ArchiveAsync(Request(MakeClip(), platform: "Azteca Radio"));

        Assert.StartsWith("mexico/ecopetrol/2026/10/14/", result.Path!);
    }

    [Fact]
    public async Task ArchiveAsync_refuses_what_is_not_a_note_clip()
    {
        var radio = new FakeStorage();
        var service = Service(radio, null);

        Assert.NotNull((await service.ArchiveAsync(Request(MakeClip("secrets.mp3")))).Error);                  // not a fragment_<id> file
        Assert.NotNull((await service.ArchiveAsync(Request(MakeClip("fragment_abc.txt")))).Error);              // not mp3 or mp4
        Assert.NotNull((await service.ArchiveAsync(Request(Path.Combine(Path.GetTempPath(), "fragment_none.mp3")))).Error); // does not exist
        Assert.NotNull((await service.ArchiveAsync(Request(MakeClip(), medium: "internet"))).Error);          // no clip for the press
        Assert.Empty(radio.Uploaded);
    }

    [Fact]
    public async Task ArchiveAsync_says_so_when_the_medium_has_no_account_or_the_upload_fails()
    {
        var clip = MakeClip();

        var noAccount = await Service(radio: null, television: new FakeStorage()).ArchiveAsync(Request(clip));
        Assert.Contains("cuenta", noAccount.Error);

        var failing = await Service(new FakeStorage { Succeeds = false }, null).ArchiveAsync(Request(clip));
        Assert.Contains("subir", failing.Error);
    }
}
