namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// HTTP response body for GET /capture/status?date=yyyy-MM-dd — per-source, per-hour capture
/// coverage for one day, consumed by the web-ui capture status page.
/// </summary>
public sealed class CaptureStatusResponse
{
    public string Date { get; set; } = string.Empty;

    public IReadOnlyList<CaptureSourceStatusEntry> Sources { get; set; } = [];
}

public sealed class CaptureSourceStatusEntry
{
    public string SourceId { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string Media { get; set; } = string.Empty;

    public bool IsExcluded { get; set; }

    public IReadOnlyList<CaptureHourStatusEntry> Hours { get; set; } = [];
}

public sealed class CaptureHourStatusEntry
{
    public int Hour { get; set; }

    public string Status { get; set; } = string.Empty;

    public double? CoveragePercent { get; set; }

    // Sub-hour timeline (captured/gap/no-session slices), when checkpoint data is available for
    // this hour — null for hours evidenced before this feature existed, or with no session at all.
    public IReadOnlyList<CaptureSegmentEntry>? Segments { get; set; }

    // Why the recording has gaps this hour: failures, retries and recoveries, oldest first, with
    // runs of identical failed retries already collapsed. Empty when nothing was recorded.
    public IReadOnlyList<CaptureIncidentEntry> Incidents { get; set; } = [];
}

public sealed class CaptureIncidentEntry
{
    // ISO-8601 in Bogota time, so clients can read HH:mm straight from the text.
    public string At { get; set; } = string.Empty;

    // Last retry of a collapsed run of identical failed retries; null for a single event.
    public string? Until { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public int Count { get; set; } = 1;
}

public sealed class CaptureSegmentEntry
{
    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public string State { get; set; } = string.Empty;
}
