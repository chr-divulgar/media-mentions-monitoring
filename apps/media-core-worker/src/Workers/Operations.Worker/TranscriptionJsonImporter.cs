using System.Globalization;
using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed record TranscriptionImportResult(int Files, int Entries, int Failed);

/// <summary>
/// The transcription JSON files that were on disk (one per hour and source, next to its audio) go into the
/// `transcription` collection once. Source and medium come from the folder the file is in, the platform from the source
/// catalog (the source id when it is not there), and the hour file is the .opus next to the JSON, as the alerts have it.
/// Saving is by hour file and times, so it can run again without duplicating. It runs at startup only while
/// importTranscriptionJson is on in worker-options.json: it is turned on once and off again.
/// </summary>
public sealed class TranscriptionJsonImporter : BackgroundService
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true };

    private readonly OperationsWorkerOptions options;
    private readonly ITranscriptionStore store;
    private readonly ILogger<TranscriptionJsonImporter> logger;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> platformOf;

    public TranscriptionJsonImporter(
        OperationsWorkerOptions options,
        ITranscriptionStore store,
        ILogger<TranscriptionJsonImporter> logger,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? platformOf = null)
    {
        this.options = options;
        this.store = store;
        this.logger = logger;
        this.platformOf = platformOf ?? (_ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ImportTranscriptionJson)
        {
            return;
        }

        try
        {
            // Let the capture start first: this is housekeeping.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            var result = await ImportAsync(stoppingToken).ConfigureAwait(false);
            logger.LogInformation(
                "[TranscriptionJsonImporter] Imported {Entries} transcription(s) from {Files} file(s); {Failed} file(s) could not be read. Turn importTranscriptionJson off.",
                result.Entries, result.Files, result.Failed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping; the import is safe to run again.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[TranscriptionJsonImporter] The import stopped because of an error.");
        }
    }

    public async Task<TranscriptionImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        var platforms = await platformOf(cancellationToken).ConfigureAwait(false);
        var files = 0;
        var entries = 0;
        var failed = 0;

        foreach (var (dayDir, _) in RetentionPurge.EnumerateDayFolders(options.AudioOutputRootPath))
        {
            var medium = ArchiveUpload.ToRelativePath(options.AudioOutputRootPath, dayDir).Split('/')[0];
            foreach (var json in JsonFilesOf(dayDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = Read(json, medium, platforms);
                if (batch is null)
                {
                    failed++;
                    continue;
                }

                await store.AppendManyAsync(batch, cancellationToken).ConfigureAwait(false);
                files++;
                entries += batch.Count;
                if (files % 1000 == 0)
                {
                    logger.LogInformation("[TranscriptionJsonImporter] {Files} file(s) imported so far.", files);
                }
            }
        }

        return new TranscriptionImportResult(files, entries, failed);
    }

    private static IEnumerable<string> JsonFilesOf(string dayDir)
    {
        try
        {
            return Directory.EnumerateFiles(dayDir, "*.json", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Null when the file cannot be read as a list of entries; an entry with a time that is not a time is left out.
    private List<TranscriptionEntry>? Read(string jsonPath, string medium, IReadOnlyDictionary<string, string> platforms)
    {
        try
        {
            using var stream = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sourceId = new DirectoryInfo(Path.GetDirectoryName(jsonPath)!).Name;
            var platform = platforms.TryGetValue(sourceId, out var name) ? name : sourceId;
            var hourFile = Path.ChangeExtension(jsonPath, ".opus");
            var result = new List<TranscriptionEntry>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryTime(element, "startTime", out var start) && TryTime(element, "endTime", out var end))
                {
                    result.Add(new TranscriptionEntry(
                        sourceId, platform, medium, hourFile, start, end,
                        element.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                        element.TryGetProperty("status", out var status) ? status.GetString() ?? "recognized" : "recognized"));
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("[TranscriptionJsonImporter] Could not read {Path}: {Reason}", jsonPath, exception.Message);
            return null;
        }
    }

    private static bool TryTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
