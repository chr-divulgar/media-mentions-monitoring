using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Append-only incident log, one JSON-lines file per Bogota-local day. Today's incidents are also
/// kept in memory so the status endpoint, polled every few seconds, never reads the disk; other days
/// are read from their file on demand. It is deliberately separate from the hourly capture summary:
/// a source that was down all hour has no row there, which is exactly when the reason matters.
/// </summary>
public sealed class SourceIncidentLog : ISourceIncidentRecorder, ISourceIncidentReader
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string directory;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private DateOnly cachedDay;
    private List<SourceIncident>? cachedIncidents;

    public SourceIncidentLog(string directory, Func<DateTimeOffset>? clock = null)
    {
        this.directory = directory;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Record(string sourceId, string kind, string? reason = null)
    {
        var at = clock().ToOffset(BogotaOffset);
        var incident = new SourceIncident(at, sourceId, kind, IncidentReason.Humanize(reason));

        lock (gate)
        {
            var today = DateOnly.FromDateTime(at.DateTime);
            var list = LoadDay(today);
            list.Add(incident);

            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(PathFor(today), JsonSerializer.Serialize(incident, SerializerOptions) + Environment.NewLine);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Losing the file copy only costs persistence across restarts; it must not break capture.
            }
        }
    }

    public IReadOnlyList<SourceIncident> ListForDay(DateOnly date)
    {
        lock (gate)
        {
            var today = DateOnly.FromDateTime(clock().ToOffset(BogotaOffset).DateTime);
            return (date == today ? LoadDay(date) : ReadFile(date)).ToArray();
        }
    }

    // Only today is cached: it is the only day that changes and the only one polled continuously.
    private List<SourceIncident> LoadDay(DateOnly day)
    {
        if (cachedIncidents is null || cachedDay != day)
        {
            cachedDay = day;
            cachedIncidents = ReadFile(day);
        }

        return cachedIncidents;
    }

    private List<SourceIncident> ReadFile(DateOnly day)
    {
        var path = PathFor(day);
        var incidents = new List<SourceIncident>();

        try
        {
            if (!File.Exists(path))
            {
                return incidents;
            }

            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SourceIncident>(line, SerializerOptions) is { } incident)
                    {
                        incidents.Add(incident);
                    }
                }
                catch (JsonException)
                {
                    // A torn last line after a crash must not hide the rest of the day.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable file: show what is in memory rather than failing the status page.
        }

        return incidents;
    }

    private string PathFor(DateOnly day) => Path.Combine(directory, $"incidents-{day:yyyy-MM-dd}.jsonl");
}
