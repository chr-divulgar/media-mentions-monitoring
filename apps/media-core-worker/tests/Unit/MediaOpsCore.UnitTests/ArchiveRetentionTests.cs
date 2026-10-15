using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The archive keeps radio and TV for a limited time (90 days by default); older day folders are deleted.
// The server's base folder is shared with a website, so these tests are mostly about what must NEVER be deleted.
public sealed class ArchiveRetentionTests
{
    // 2026-10-10 in Bogota: 90 days back is 2026-07-12, so a day folder is expired when it is dated before that.
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Today = new(2026, 10, 10);

    // An archive made of folders only: enough for listing and deleting directories.
    private sealed class FakeTree(params string[] files) : IArchiveStorage
    {
        private readonly HashSet<string> paths = new(files, StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public bool ListThrows { get; set; }
        public Func<string, bool> DeleteThrowsFor { get; set; } = _ => false;

        public Task<bool> UploadAsync(string localPath, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> DownloadAsync(string relativePath, string localPath, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDirectory, CancellationToken cancellationToken = default)
        {
            if (ListThrows) throw new IOException("server down");
            var prefix = relativeDirectory.Length == 0 ? string.Empty : relativeDirectory + "/";
            IReadOnlyList<string> children = paths
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path.IndexOf('/', prefix.Length) > 0)
                .Select(path => path[prefix.Length..path.IndexOf('/', prefix.Length)])
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(children);
        }

        public Task DeleteDirectoryAsync(string relativeDirectory, CancellationToken cancellationToken = default)
        {
            if (DeleteThrowsFor(relativeDirectory)) throw new IOException("denied");
            paths.RemoveWhere(path => path.StartsWith(relativeDirectory + "/", StringComparison.Ordinal));
            Deleted.Add(relativeDirectory);
            return Task.CompletedTask;
        }

        public bool Has(string prefix) => paths.Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    // One shared account (as before the media had their own) and the same days for both media, unless told otherwise.
    private static ArchiveRetentionWorker Worker(FakeTree storage, ArchiveFixture.Incidents? incidents = null, int retentionDays = 90) =>
        Worker(ArchiveFixture.Storages.All(storage), incidents, retentionDays, retentionDays);

    private static ArchiveRetentionWorker Worker(IArchiveStorageProvider storages, ArchiveFixture.Incidents? incidents, int radioDays, int televisionDays) =>
        new(
            new OperationsWorkerOptions { ArchiveRetentionDaysRadio = radioDays, ArchiveRetentionDaysTelevision = televisionDays },
            storages, incidents ?? new ArchiveFixture.Incidents(), NullLogger<ArchiveRetentionWorker>.Instance, () => Now);

    [Fact]
    public async Task FindExpiredDayFoldersAsync_returns_the_days_of_that_medium_older_than_the_retention_oldest_first()
    {
        var tree = new FakeTree(
            "radio/2026/07/11/wradio/a.mp3",       // 91 days old: expired
            "radio/2026/07/12/wradio/a.mp3",       // exactly 90 days: kept
            "radio/2026/10/09/wradio/a.mp3",       // yesterday
            "television/2026/04/01/rcn_tv/a.mp4",  // another medium's, even if expired
            "radio/2025/12/31/wradio/a.mp3");      // expired, previous year

        var expired = await ArchiveRemoteRetention.FindExpiredDayFoldersAsync(tree, ArchiveMedia.Radio, Today, 90, CancellationToken.None);

        Assert.Equal(["radio/2025/12/31", "radio/2026/07/11"], expired);
    }

    [Fact]
    public async Task FindExpiredDayFoldersAsync_never_returns_anything_that_is_not_a_media_day_folder()
    {
        // The base folder is shared with a website: only the two media roots, only year/month/day names.
        var tree = new FakeTree(
            "public_html/2020/01/01/index.html",
            "internet/2020/01/01/x/a.pdf",
            "radio/readme/01/01/a.txt",
            "radio/2020/ab/01/a.txt",
            "radio/2020/01/xx/a.txt",
            "radio/2020/13/45/a.txt",             // not a real date
            "radio/2020/01/a-file-not-a-folder");

        foreach (var medium in new[] { ArchiveMedia.Radio, ArchiveMedia.Television })
        {
            Assert.Empty(await ArchiveRemoteRetention.FindExpiredDayFoldersAsync(tree, medium, Today, 90, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RunOnceAsync_deletes_the_expired_day_folders_and_leaves_everything_else()
    {
        var tree = new FakeTree(
            "radio/2026/04/01/wradio/a.mp3",
            "television/2026/04/02/rcn_tv/a.mp4",
            "radio/2026/10/09/wradio/a.mp3",
            "public_html/index.html",
            "public_html/2020/01/01/legacy.html");

        await Worker(tree).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["radio/2026/04/01", "television/2026/04/02"], tree.Deleted.OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(tree.Has("radio/2026/04/01/"));
        Assert.True(tree.Has("radio/2026/10/09/"));
        Assert.True(tree.Has("public_html/"));
    }

    [Fact]
    public async Task RunOnceAsync_applies_each_media_own_days_and_only_touches_its_own_account()
    {
        // Radio keeps 10 days, TV only 7: an 8-day-old day is gone from TV and kept in radio. Each medium has its own
        // account, so the radio account is never asked to delete TV's folders (or the other way round).
        var radio = new FakeTree("radio/2026/10/02/wradio/a.opus", "radio/2026/09/20/wradio/a.opus");
        var television = new FakeTree("television/2026/10/02/rcn_tv/a.mp4", "television/2026/10/09/rcn_tv/a.mp4");
        var press = new FakeTree("notes/internet/2020/01/01/x.pdf");

        await Worker(new ArchiveFixture.Storages(radio, television, press), incidents: null, radioDays: 10, televisionDays: 7).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["radio/2026/09/20"], radio.Deleted);
        Assert.Equal(["television/2026/10/02"], television.Deleted);
        Assert.Empty(press.Deleted); // the notes are never purged
    }

    [Fact]
    public async Task RunOnceAsync_skips_a_medium_without_an_account_and_one_whose_days_are_zero()
    {
        var television = new FakeTree("television/2020/01/01/rcn_tv/a.mp4");

        await Worker(new ArchiveFixture.Storages(radio: null, television), incidents: null, radioDays: 10, televisionDays: 0).RunOnceAsync(CancellationToken.None);

        Assert.Empty(television.Deleted); // television keeps everything; radio has no account to delete from
    }

    [Fact]
    public async Task RunOnceAsync_deletes_at_most_five_day_folders_per_pass()
    {
        var tree = new FakeTree(Enumerable.Range(1, 8).Select(day => $"radio/2026/03/{day:00}/wradio/a.mp3").ToArray());
        var worker = Worker(tree);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(5, tree.Deleted.Count);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(8, tree.Deleted.Count);
    }

    [Fact]
    public async Task RunOnceAsync_does_nothing_when_retention_is_zero_for_both()
    {
        var tree = new FakeTree("radio/2020/01/01/wradio/a.mp3");

        await Worker(tree, retentionDays: 0).RunOnceAsync(CancellationToken.None);

        Assert.Empty(tree.Deleted);
    }

    [Fact]
    public async Task RunOnceAsync_raises_a_too_short_retention_to_seven_days()
    {
        // A typo such as 3 must not wipe last week's archive.
        var tree = new FakeTree(
            "radio/2026/10/05/wradio/a.mp3",   // 5 days old
            "radio/2026/10/01/wradio/a.mp3");  // 9 days old

        await Worker(tree, retentionDays: 3).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["radio/2026/10/01"], tree.Deleted);
    }

    [Fact]
    public async Task RunOnceAsync_deletes_nothing_when_the_listing_fails()
    {
        var tree = new FakeTree("radio/2020/01/01/wradio/a.mp3") { ListThrows = true };
        var incidents = new ArchiveFixture.Incidents();

        await Worker(tree, incidents).RunOnceAsync(CancellationToken.None);

        Assert.Empty(tree.Deleted);
    }

    [Fact]
    public async Task RunOnceAsync_reports_a_folder_it_could_not_delete_and_carries_on_with_the_rest()
    {
        var tree = new FakeTree("radio/2026/03/01/wradio/a.mp3", "radio/2026/03/02/wradio/a.mp3")
        {
            DeleteThrowsFor = path => path.EndsWith("03/01", StringComparison.Ordinal),
        };
        var incidents = new ArchiveFixture.Incidents();

        await Worker(tree, incidents).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["radio/2026/03/02"], tree.Deleted);
        Assert.Single(incidents.Records, r => r.Kind == IncidentKinds.PurgeFailed && r.Reason!.Contains("radio/2026/03/01"));
    }
}
