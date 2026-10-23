using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The PDF of a web note is made when the alert is created and kept on this machine for the current day and the one
// before; it goes to the archive only when the note is saved, so an alert nobody edits never uploads anything.
public sealed class LocalNotePdfStoreTests : IDisposable
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"note-pdfs-{Guid.NewGuid():N}");
    private DateTimeOffset now = new(2026, 10, 22, 14, 0, 0, Bogota);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakePrinter : IBrowserPagePrinter
    {
        public bool Succeeds { get; set; } = true;
        public List<(string Url, IReadOnlyList<string>? Terms)> Printed { get; } = [];

        public Task<bool> PrintPdfAsync(string url, string outputPath, IReadOnlyList<string>? highlightTerms = null, CancellationToken cancellationToken = default)
        {
            Printed.Add((url, highlightTerms));
            if (!Succeeds) return Task.FromResult(false);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "%PDF-fake");
            return Task.FromResult(true);
        }
    }

    private LocalNotePdfStore Store(FakePrinter printer) =>
        new(printer, root, NullLogger<LocalNotePdfStore>.Instance, () => now);

    private static WebNotePdfRequest Request(string alertId = "A1", string url = "https://site.co/a") =>
        new(alertId, url, ["petroleo"], new DateTimeOffset(2026, 10, 22, 13, 59, 0, Bogota));

    [Fact]
    public async Task A_requested_pdf_is_printed_with_the_alerts_words_and_kept_under_the_day_of_the_alert()
    {
        var printer = new FakePrinter();
        var store = Store(printer);

        await store.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal([("https://site.co/a", (IReadOnlyList<string>?)new[] { "petroleo" })], printer.Printed.Select(p => (p.Url, p.Terms)));
        Assert.Equal(Path.Combine(root, "2026-10-22", "A1.pdf"), store.Find("A1"));
        Assert.True(File.Exists(store.Find("A1")!));
    }

    [Fact]
    public async Task A_pdf_that_could_not_be_printed_leaves_nothing_and_does_not_fail()
    {
        var store = Store(new FakePrinter { Succeeds = false });

        await store.GenerateAsync(Request(), CancellationToken.None);

        Assert.Null(store.Find("A1"));
        Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Asking_twice_for_the_same_alert_prints_once()
    {
        var printer = new FakePrinter();
        var store = Store(printer);

        await store.GenerateAsync(Request(), CancellationToken.None);
        await store.GenerateAsync(Request(), CancellationToken.None);

        Assert.Single(printer.Printed);
    }

    [Fact]
    public async Task Find_looks_in_the_current_day_and_the_one_before_only()
    {
        var store = Store(new FakePrinter());
        await store.GenerateAsync(Request("TODAY"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(root, "2026-10-21"));
        File.WriteAllText(Path.Combine(root, "2026-10-21", "YESTERDAY.pdf"), "%PDF");
        Directory.CreateDirectory(Path.Combine(root, "2026-10-20"));
        File.WriteAllText(Path.Combine(root, "2026-10-20", "OLD.pdf"), "%PDF");

        Assert.NotNull(store.Find("TODAY"));
        Assert.NotNull(store.Find("YESTERDAY"));
        Assert.Null(store.Find("OLD"));
        Assert.Null(store.Find("../../etc/passwd")); // an id is never a path
    }

    [Fact]
    public void PurgeOld_deletes_the_days_before_yesterday_and_nothing_else()
    {
        foreach (var day in new[] { "2026-10-19", "2026-10-20", "2026-10-21", "2026-10-22", "notes", "2026-13-40" })
        {
            Directory.CreateDirectory(Path.Combine(root, day));
            File.WriteAllText(Path.Combine(root, day, "x.pdf"), "%PDF");
        }

        Store(new FakePrinter()).PurgeOld();

        Assert.Equal(["2026-10-21", "2026-10-22", "2026-13-40", "notes"], Directory.GetDirectories(root).Select(directory => Path.GetFileName(directory)!).Order().ToArray());
    }

    [Fact]
    public async Task Requests_are_made_in_the_background_and_never_throw()
    {
        var printer = new FakePrinter();
        var store = Store(printer);

        store.Request(Request());
        for (var i = 0; i < 100 && store.Find("A1") is null; i++) await Task.Delay(50);

        Assert.NotNull(store.Find("A1"));
    }
}
