using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed record SourceIncident(DateTimeOffset At, string SourceId, string Kind, string? Reason);

public interface ISourceIncidentReader
{
    /// <summary>Incidents of one Bogota-local day, oldest first.</summary>
    IReadOnlyList<SourceIncident> ListForDay(DateOnly date);
}

/// <summary>Turns a raw failure message into the one line an operator can act on.</summary>
public static partial class IncidentReason
{
    private const int MaxLength = 300;

    // FFmpeg codes seen in production. Small numbers are POSIX errno as returned on Windows (negated);
    // the large ones are FFmpeg's own four-byte error tags (FFERRTAG), e.g. the HTTP status of the
    // radio's server, which the log only ever showed as an opaque number.
    private static readonly Dictionary<int, string> FriendlyFfmpegCodes = new()
    {
        [-28] = "Disco sin espacio",
        [-110] = "Tiempo de conexión agotado",
        [-138] = "Tiempo de conexión agotado",
        [FfmpegTag(0xF8, '4', '0', '0')] = "El servidor rechazó la solicitud (HTTP 400)",
        [FfmpegTag(0xF8, '4', '0', '1')] = "El servidor exige autenticación (HTTP 401)",
        [FfmpegTag(0xF8, '4', '0', '3')] = "El servidor prohíbe el acceso (HTTP 403)",
        [FfmpegTag(0xF8, '4', '0', '4')] = "El stream ya no existe en esa URL (HTTP 404)",
        [FfmpegTag(0xF8, '4', 'X', 'X')] = "El servidor rechazó la solicitud (HTTP 4xx)",
        [FfmpegTag(0xF8, '5', 'X', 'X')] = "Error del servidor de la emisora (HTTP 5xx)",
        [FfmpegTag('I', 'N', 'D', 'A')] = "Datos inválidos en el stream",
        [FfmpegTag('E', 'O', 'F', ' ')] = "Fin de la transmisión",
    };

    // FFmpeg's FFERRTAG(a, b, c, d): the four characters packed little-endian, negated.
    private static int FfmpegTag(int a, char b, char c, char d) => -(a | (b << 8) | (c << 16) | (d << 24));

    public static string? Humanize(string? raw)
    {
        var firstLine = raw?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        // Trailing periods go: callers put the reason inside their own sentence.
        var text = ExceptionPrefix().Replace(firstLine, string.Empty).TrimEnd('.', ' ');

        if (text.StartsWith("Stream ended (EOF)", StringComparison.OrdinalIgnoreCase))
        {
            return "El servidor cerró la conexión (EOF)";
        }

        if (text.Contains("not currently live", StringComparison.OrdinalIgnoreCase))
        {
            text = $"El canal no está en vivo ({text})";
        }
        else if (FfmpegCode().Match(text) is { Success: true } match
                 && FriendlyFfmpegCodes.TryGetValue(int.Parse(match.Groups[1].Value), out var friendly))
        {
            text = $"{friendly} ({text})";
        }

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    [GeneratedRegex(@"^[\w.]+Exception:\s*")]
    private static partial Regex ExceptionPrefix();

    [GeneratedRegex(@"FFmpeg error code (-\d+)")]
    private static partial Regex FfmpegCode();
}

public static class IncidentCollapser
{
    /// <summary>
    /// One source's incidents (plus worker-wide ones), oldest first. Consecutive failed retries with
    /// the same reason become one row with a range and a count — a source down for an hour retries
    /// every minute, and 59 identical lines would bury the one that changes.
    /// </summary>
    public static IReadOnlyList<CaptureIncidentEntry> Collapse(IEnumerable<SourceIncident> incidents)
    {
        var rows = new List<CaptureIncidentEntry>();

        foreach (var incident in incidents)
        {
            var previous = rows.Count > 0 ? rows[^1] : null;
            if (previous is not null
                && incident.Kind == IncidentKinds.RetryFailed
                && previous.Kind == IncidentKinds.RetryFailed
                && previous.Reason == incident.Reason)
            {
                previous.Until = Format(incident.At);
                previous.Count++;
            }
            else
            {
                rows.Add(new CaptureIncidentEntry
                {
                    At = Format(incident.At),
                    Kind = incident.Kind,
                    Reason = incident.Reason,
                });
            }
        }

        return rows;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToOffset(TimeSpan.FromHours(-5)).ToString("o");
}
