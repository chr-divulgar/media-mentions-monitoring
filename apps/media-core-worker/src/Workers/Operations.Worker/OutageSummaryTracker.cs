using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Turns a per-minute retry loop into a short story: one line when a source goes down, nothing
/// while the same reason repeats, one line when the reason changes and one when it recovers
/// ("no estaba en vivo de 09:00 a 09:10, 11 intentos"), plus a reminder each hour while it is
/// still down. It also tells the caller whether an incident should be recorded, so the incident
/// log is not filled with one "retry-failed" per minute either.
/// </summary>
public sealed class OutageSummaryTracker(
    ILogger logger,
    TimeSpan localOffset,
    Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Outage> outages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    private sealed class Outage(DateTimeOffset startedAt, string reason)
    {
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public string Reason { get; set; } = reason;
        public int Attempts { get; set; }
        public DateTimeOffset LastReminderHour { get; set; } = HourOf(startedAt);
    }

    /// <summary>A retry failed. Returns true when it is new information (first failure or a new reason).</summary>
    public bool RecordFailure(string sourceId, string? reason)
    {
        var normalized = IncidentReason.Humanize(reason) ?? "sin detalle";
        var now = clock();

        lock (gate)
        {
            if (!outages.TryGetValue(sourceId, out var outage))
            {
                outages[sourceId] = new Outage(now, normalized) { Attempts = 1 };
                logger.LogWarning(
                    "Fuente {SourceId} sin servicio desde las {Since}: {Reason}. Se reintenta cada minuto.",
                    sourceId, Format(now), normalized);
                return true;
            }

            outage.Attempts++;

            if (!outage.Reason.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Fuente {SourceId}: de {Since} a {Until} {Reason} ({Attempts} intentos). Ahora: {NewReason}.",
                    sourceId, Format(outage.StartedAt), Format(now), outage.Reason, outage.Attempts - 1, normalized);
                outage.Reason = normalized;
                outage.Attempts = 1;
                outage.StartedAt = now;
                outage.LastReminderHour = HourOf(now);
                return true;
            }

            if (HourOf(now) > outage.LastReminderHour)
            {
                outage.LastReminderHour = HourOf(now);
                logger.LogWarning(
                    "Fuente {SourceId} sigue sin servicio desde las {Since}: {Reason} ({Attempts} intentos).",
                    sourceId, Format(outage.StartedAt), outage.Reason, outage.Attempts);
            }

            return false;
        }
    }

    /// <summary>The source is back. Returns the human summary of the outage, or null if none was open.</summary>
    public string? RecordRecovery(string sourceId)
    {
        var now = clock();

        lock (gate)
        {
            if (!outages.Remove(sourceId, out var outage))
            {
                return null;
            }

            var summary = $"{outage.Reason}, de {Format(outage.StartedAt)} a {Format(now)} ({outage.Attempts} intentos)";
            logger.LogInformation("Fuente {SourceId} recuperada. Estuvo sin servicio: {Summary}.", sourceId, summary);
            return summary;
        }
    }

    private string Format(DateTimeOffset value) => value.ToOffset(localOffset).ToString("HH:mm");

    private static DateTimeOffset HourOf(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);
}
