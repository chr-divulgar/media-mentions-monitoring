using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class OutageSummaryTrackerTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    // UTC-5, like Bogota: 14:00Z is 09:00 local.
    private static readonly DateTimeOffset Nine = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);

    private readonly CapturingLogger logger = new();
    private DateTimeOffset now = Nine;

    private OutageSummaryTracker Build() => new(logger, TimeSpan.FromHours(-5), () => now);

    [Fact]
    public void Only_the_first_failure_of_an_outage_is_new_and_logged()
    {
        var tracker = Build();

        Assert.True(tracker.RecordFailure("cable", "no está en vivo"));
        for (var minute = 1; minute <= 9; minute++)
        {
            now = Nine.AddMinutes(minute);
            Assert.False(tracker.RecordFailure("cable", "no está en vivo"));
        }

        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("cable", line.Message);
        Assert.Contains("09:00", line.Message);
        Assert.Contains("cada minuto", line.Message);
    }

    [Fact]
    public void The_reason_is_humanised_and_has_no_doubled_period()
    {
        var tracker = Build();

        tracker.RecordFailure("policia", "avformat_open_input failed with FFmpeg error code -1482175992.");

        var message = logger.Lines[0].Message;
        Assert.Contains("Error del servidor de la emisora (HTTP 5xx)", message);
        Assert.DoesNotContain("..", message);
    }

    [Fact]
    public void Recovery_summarises_the_whole_outage_once()
    {
        var tracker = Build();
        tracker.RecordFailure("cable", "no está en vivo");
        for (var minute = 1; minute <= 10; minute++)
        {
            now = Nine.AddMinutes(minute);
            tracker.RecordFailure("cable", "no está en vivo");
        }

        var summary = tracker.RecordRecovery("cable");

        Assert.Equal("no está en vivo, de 09:00 a 09:10 (11 intentos)", summary);
        Assert.Equal(LogLevel.Information, logger.Lines[^1].Level);
        Assert.Contains("09:00 a 09:10", logger.Lines[^1].Message);
        Assert.Null(tracker.RecordRecovery("cable"));
    }

    [Fact]
    public void A_new_reason_closes_the_previous_stretch_and_counts_as_new()
    {
        var tracker = Build();
        tracker.RecordFailure("rcn", "no está en vivo");
        now = Nine.AddMinutes(5);
        tracker.RecordFailure("rcn", "no está en vivo");
        now = Nine.AddMinutes(6);

        Assert.True(tracker.RecordFailure("rcn", "YouTube exige autenticación"));

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("de 09:00 a 09:06", logger.Lines[1].Message);
        Assert.Contains("2 intentos", logger.Lines[1].Message);
    }

    [Fact]
    public void A_long_outage_is_reminded_once_per_hour()
    {
        var tracker = Build();
        tracker.RecordFailure("rcn", "no está en vivo");

        for (var minute = 1; minute <= 125; minute++)
        {
            now = Nine.AddMinutes(minute);
            tracker.RecordFailure("rcn", "no está en vivo");
        }

        // The first line plus a reminder at 10:00 and at 11:00.
        Assert.Equal(3, logger.Lines.Count);
        Assert.Contains("sigue sin servicio", logger.Lines[1].Message);
    }

    [Fact]
    public void Sources_are_tracked_independently()
    {
        var tracker = Build();

        Assert.True(tracker.RecordFailure("a", "no está en vivo"));
        Assert.True(tracker.RecordFailure("b", "no está en vivo"));
        Assert.False(tracker.RecordFailure("a", "no está en vivo"));
    }
}
