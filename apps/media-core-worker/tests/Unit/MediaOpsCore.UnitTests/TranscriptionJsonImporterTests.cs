using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The transcription JSON files that are on disk today (one per hour and source, next to its audio) go into the
// `transcription` collection once. It can be run again without duplicating anything: the store saves by hour file and times.
public sealed class TranscriptionJsonImporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "json-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class KeyedStore : ITranscriptionStore
    {
        private readonly Dictionary<string, TranscriptionEntry> byKey = [];
        public int Batches { get; private set; }

        public IReadOnlyCollection<TranscriptionEntry> Entries => byKey.Values;

        public Task<IReadOnlyList<TranscriptionEntry>> LoadHourAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranscriptionEntry>>(byKey.Values.Where(e => e.FilePath == filePath).ToArray());

        public Task AppendAsync(TranscriptionEntry entry, CancellationToken cancellationToken = default) =>
            AppendManyAsync([entry], cancellationToken);

        public Task AppendManyAsync(IReadOnlyList<TranscriptionEntry> entries, CancellationToken cancellationToken = default)
        {
            Batches++;
            foreach (var entry in entries)
            {
                byKey[$"{entry.FilePath}|{entry.StartTime:O}|{entry.EndTime:O}"] = entry;
            }

            return Task.CompletedTask;
        }
    }

    private string WriteJson(string medium, string day, string source, string hour, string json)
    {
        var date = DateTime.Parse(day);
        var directory = Path.Combine(root, medium, $"{date:yyyy}", $"{date:MM}", $"{date:dd}", source);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{source}_{day}_{hour}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoEntries = """
        [
          { "text": "hoy habla el presidente", "startTime": "2026-11-10T10:00:12.000-05:00", "endTime": "2026-11-10T10:00:30.000-05:00", "status": "recognized" },
          { "text": "", "startTime": "2026-11-10T10:00:30.000-05:00", "endTime": "2026-11-10T10:00:48.000-05:00", "status": "no_speech" }
        ]
        """;

    private TranscriptionJsonImporter Build(KeyedStore store, Dictionary<string, string>? platforms = null) =>
        new(new OperationsWorkerOptions { AudioOutputRootPath = root }, store, NullLogger<TranscriptionJsonImporter>.Instance,
            platformOf: _ => Task.FromResult<IReadOnlyDictionary<string, string>>(platforms ?? []));

    [Fact]
    public async Task Imports_each_entry_with_its_source_medium_platform_and_the_audio_file_of_its_hour()
    {
        var json = WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", TwoEntries);
        var store = new KeyedStore();

        var result = await Build(store, new() { ["radio-a"] = "Caracol Radio" }).ImportAsync(CancellationToken.None);

        Assert.Equal(1, result.Files);
        Assert.Equal(2, result.Entries);
        var first = store.Entries.Single(e => e.Text == "hoy habla el presidente");
        Assert.Equal("radio-a", first.SourceId);
        Assert.Equal("radio", first.Media);
        Assert.Equal("Caracol Radio", first.Platform);
        Assert.Equal(Path.ChangeExtension(json, ".opus"), first.FilePath);
        Assert.Equal("recognized", first.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 10, 0, 12, TimeSpan.FromHours(-5)), first.StartTime);
        Assert.Equal("no_speech", store.Entries.Single(e => e.Text == "").Status);
    }

    [Fact]
    public async Task Uses_the_source_id_as_platform_when_the_catalog_does_not_have_it()
    {
        WriteJson("radio", "2026-11-10", "old-station", "10-00-00", TwoEntries);
        var store = new KeyedStore();

        await Build(store).ImportAsync(CancellationToken.None);

        Assert.All(store.Entries, entry => Assert.Equal("old-station", entry.Platform));
    }

    [Fact]
    public async Task Takes_the_medium_from_the_folder_it_is_in()
    {
        WriteJson("television", "2026-11-10", "canal-1", "10-00-00", TwoEntries);
        var store = new KeyedStore();

        await Build(store).ImportAsync(CancellationToken.None);

        Assert.All(store.Entries, entry => Assert.Equal("television", entry.Media));
    }

    [Fact]
    public async Task Running_it_again_does_not_duplicate_anything()
    {
        WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", TwoEntries);
        WriteJson("radio", "2026-11-10", "radio-b", "10-00-00", TwoEntries);
        var store = new KeyedStore();
        var importer = Build(store);

        await importer.ImportAsync(CancellationToken.None);
        var second = await importer.ImportAsync(CancellationToken.None);

        Assert.Equal(4, store.Entries.Count);
        Assert.Equal(4, second.Entries);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_counted_and_the_others_are_still_imported()
    {
        WriteJson("radio", "2026-11-10", "radio-a", "09-00-00", "{ this is not json");
        WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", TwoEntries);
        var store = new KeyedStore();

        var result = await Build(store).ImportAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Files);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public async Task Ignores_an_entry_with_a_time_that_is_not_a_time_and_keeps_the_rest_of_the_file()
    {
        WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", """
            [
              { "text": "bad", "startTime": "ayer", "endTime": "hoy", "status": "recognized" },
              { "text": "good", "startTime": "2026-11-10T10:00:12.000-05:00", "endTime": "2026-11-10T10:00:30.000-05:00", "status": "recognized" }
            ]
            """);
        var store = new KeyedStore();

        var result = await Build(store).ImportAsync(CancellationToken.None);

        Assert.Equal(["good"], store.Entries.Select(e => e.Text));
        Assert.Equal(1, result.Entries);
    }

    [Fact]
    public async Task Saves_each_file_in_one_batch_not_entry_by_entry()
    {
        WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", TwoEntries);
        WriteJson("radio", "2026-11-10", "radio-a", "11-00-00", TwoEntries.Replace("10:00", "11:00"));
        var store = new KeyedStore();

        await Build(store).ImportAsync(CancellationToken.None);

        Assert.Equal(2, store.Batches);
    }

    [Fact]
    public async Task Stops_when_asked()
    {
        WriteJson("radio", "2026-11-10", "radio-a", "10-00-00", TwoEntries);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(new KeyedStore()).ImportAsync(cts.Token));
    }
}
