using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class SourceIncidentTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static DateTimeOffset At(int hour, int minute, int second = 0) =>
        new(2026, 10, 1, hour, minute, second, Bogota);

    private static SourceIncident Incident(string kind, string? reason, int hour, int minute, string sourceId = "caracol_tv") =>
        new(At(hour, minute), sourceId, kind, reason);

    [Fact]
    public void Humanize_should_drop_the_exception_type_and_the_stack_trace()
    {
        var raw = "System.InvalidOperationException: avformat_open_input failed with FFmpeg error code -5.\r\n   at MediaOpsCore.Foo.Bar() in C:\\x.cs:line 3";

        var reason = IncidentReason.Humanize(raw);

        Assert.Equal("avformat_open_input failed with FFmpeg error code -5.", reason);
    }

    [Theory]
    [InlineData("System.InvalidOperationException: av_interleaved_write_frame failed with FFmpeg error code -28.", "Disco sin espacio")]
    [InlineData("System.InvalidOperationException: avformat_open_input failed with FFmpeg error code -138.", "Tiempo de conexión agotado")]
    public void Humanize_should_explain_known_ffmpeg_codes_in_spanish_and_keep_the_raw_text(string raw, string friendly)
    {
        var reason = IncidentReason.Humanize(raw);

        Assert.StartsWith(friendly, reason);
        Assert.Contains("FFmpeg error code", reason);
    }

    [Theory]
    [InlineData(-875574520, "El stream ya no existe en esa URL (HTTP 404)")]
    [InlineData(-1482175992, "Error del servidor de la emisora (HTTP 5xx)")]
    [InlineData(-1094995529, "Datos inválidos en el stream")]
    public void Humanize_should_explain_ffmpeg_error_tags_seen_in_production(int code, string friendly)
    {
        var reason = IncidentReason.Humanize($"avformat_open_input failed with FFmpeg error code {code}.");

        Assert.StartsWith(friendly, reason);
    }

    [Fact]
    public void Humanize_should_explain_end_of_stream()
    {
        Assert.Equal("El servidor cerró la conexión (EOF)", IncidentReason.Humanize("Stream ended (EOF)."));
    }

    [Fact]
    public void Humanize_should_explain_a_youtube_channel_that_is_not_live()
    {
        var reason = IncidentReason.Humanize("ERROR: [youtube:tab] @CVNOTICIASTV: The channel is not currently live");

        Assert.StartsWith("El canal no está en vivo", reason);
    }

    [Fact]
    public void Humanize_should_truncate_long_text_and_return_null_for_blank()
    {
        Assert.Equal(300, IncidentReason.Humanize(new string('x', 900))!.Length);
        Assert.Null(IncidentReason.Humanize("   "));
        Assert.Null(IncidentReason.Humanize(null));
    }

    [Fact]
    public void Collapse_should_merge_consecutive_retries_with_the_same_reason()
    {
        var incidents = new[]
        {
            Incident(IncidentKinds.Failed, "EOF", 10, 1),
            Incident(IncidentKinds.RetryFailed, "sin conexión", 10, 2),
            Incident(IncidentKinds.RetryFailed, "sin conexión", 10, 3),
            Incident(IncidentKinds.RetryFailed, "sin conexión", 10, 4),
            Incident(IncidentKinds.Recovered, null, 10, 5),
        };

        var rows = IncidentCollapser.Collapse(incidents);

        Assert.Equal(3, rows.Count);
        Assert.Equal(IncidentKinds.RetryFailed, rows[1].Kind);
        Assert.Equal(3, rows[1].Count);
        Assert.Equal(At(10, 2).ToString("o"), rows[1].At);
        Assert.Equal(At(10, 4).ToString("o"), rows[1].Until);
    }

    [Fact]
    public void Collapse_should_not_merge_retries_when_the_reason_changes()
    {
        var incidents = new[]
        {
            Incident(IncidentKinds.RetryFailed, "a", 10, 2),
            Incident(IncidentKinds.RetryFailed, "b", 10, 3),
            Incident(IncidentKinds.RetryFailed, "a", 10, 4),
        };

        Assert.Equal(3, IncidentCollapser.Collapse(incidents).Count);
    }

    [Fact]
    public void Collapse_should_leave_a_single_event_without_an_end()
    {
        var row = Assert.Single(IncidentCollapser.Collapse([Incident(IncidentKinds.Failed, "EOF", 10, 1)]));

        Assert.Null(row.Until);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void Log_should_return_what_was_recorded_for_the_day_in_order()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock(At(10, 1));
        var log = new SourceIncidentLog(folder.Path, clock.Now);

        log.Record("caracol_tv", IncidentKinds.Failed, "Stream ended (EOF).");
        clock.Set(At(10, 2));
        log.Record("caracol_tv", IncidentKinds.RetryFailed, "x");

        var incidents = log.ListForDay(new DateOnly(2026, 10, 1));

        Assert.Equal([IncidentKinds.Failed, IncidentKinds.RetryFailed], incidents.Select(i => i.Kind));
        Assert.Equal("El servidor cerró la conexión (EOF)", incidents[0].Reason);
    }

    [Fact]
    public void Log_should_survive_a_restart()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock(At(10, 1));
        new SourceIncidentLog(folder.Path, clock.Now).Record("caracol_tv", IncidentKinds.Failed, "boom");

        var afterRestart = new SourceIncidentLog(folder.Path, clock.Now);

        var incident = Assert.Single(afterRestart.ListForDay(new DateOnly(2026, 10, 1)));
        Assert.Equal("caracol_tv", incident.SourceId);
        Assert.Equal(At(10, 1), incident.At);
    }

    [Fact]
    public void Log_should_read_a_past_day_from_its_file()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock(At(23, 59));
        new SourceIncidentLog(folder.Path, clock.Now).Record("a", IncidentKinds.Failed, "boom");
        clock.Set(At(0, 5).AddDays(1));

        var log = new SourceIncidentLog(folder.Path, clock.Now);

        Assert.Single(log.ListForDay(new DateOnly(2026, 10, 1)));
        Assert.Empty(log.ListForDay(new DateOnly(2026, 10, 2)));
    }

    [Fact]
    public void Log_should_never_throw_into_the_capture_flow_when_the_disk_fails()
    {
        // A path under an existing FILE cannot be created as a directory.
        using var folder = new TempFolder();
        var blocker = Path.Combine(folder.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var log = new SourceIncidentLog(Path.Combine(blocker, "sub"), new FakeClock(At(10, 1)).Now);

        var exception = Record.Exception(() => log.Record("a", IncidentKinds.Failed, "boom"));

        Assert.Null(exception);
        Assert.Single(log.ListForDay(new DateOnly(2026, 10, 1)));
    }

    private sealed class FakeClock(DateTimeOffset start)
    {
        private DateTimeOffset current = start;
        public DateTimeOffset Now() => current;
        public void Set(DateTimeOffset value) => current = value;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "incidents-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
