using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class TvRecordingLocatorTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static DateTimeOffset At(int day, int hour, int minute, int second = 0, int month = 10) =>
        new(2026, month, day, hour, minute, second, Bogota);

    private static TvRecordingCandidate Candidate(DateTimeOffset start, string sourceId = "rcn_tv") =>
        new($"television/{start:yyyy}/{start:MM}/{start:dd}/{sourceId}/{sourceId}_{start:yyyy-MM-dd_HH-mm-ss}.mp4", start);

    [Theory]
    [InlineData("rcn_tv_2026-10-05_12-05-59.mp4", "rcn_tv", true)]
    [InlineData("noticias-caracol-live_2026-10-05_12-05-59.mp4", "noticias-caracol-live", true)]
    [InlineData("RCN_TV_2026-10-05_12-05-59.MP4", "rcn_tv", true)]
    [InlineData("rcn_tv_2026-10-05_12-05-59.opus", "rcn_tv", false)]
    [InlineData("rcn_tv_2026-10-05_12-05-59.mp4.part", "rcn_tv", false)]
    [InlineData("canal_10_2026-10-05_12-05-59.mp4", "canal_1", false)] // another source whose id starts the same
    [InlineData("rcn_tv_not-a-date.mp4", "rcn_tv", false)]
    public void TryParseStart_reads_the_start_from_the_file_name_of_exactly_that_source(string fileName, string sourceId, bool expected)
    {
        var parsed = TvRecordingLocator.TryParseStart(fileName, sourceId, Bogota, out var start);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(At(5, 12, 5, 59), start);
        }
    }

    [Fact]
    public void Find_picks_the_latest_recording_that_started_before_the_instant()
    {
        var first = Candidate(At(5, 12, 5, 59));
        var restart = Candidate(At(5, 12, 40, 38));
        var next = Candidate(At(5, 13, 0, 8));

        Assert.Equal(restart, TvRecordingLocator.Find([next, first, restart], At(5, 12, 50)));
        Assert.Equal(first, TvRecordingLocator.Find([next, first, restart], At(5, 12, 20)));
        Assert.Equal(next, TvRecordingLocator.Find([next, first, restart], At(5, 13, 30)));
    }

    [Fact]
    public void Find_returns_nothing_before_the_first_recording_or_after_the_last_one_ended()
    {
        var only = Candidate(At(5, 12, 5, 59)); // planned to run until 13:00

        Assert.Null(TvRecordingLocator.Find([only], At(5, 12, 0)));
        Assert.Null(TvRecordingLocator.Find([only], At(5, 13, 0, 1)));
        Assert.Null(TvRecordingLocator.Find([], At(5, 12, 30)));
    }

    [Fact]
    public void Find_covers_a_recording_that_started_in_the_last_minute_of_the_previous_day()
    {
        // Started 23:59:30: with under a minute left it runs through the whole next hour (until 01:00).
        var overMidnight = Candidate(At(4, 23, 59, 30));

        Assert.Equal(overMidnight, TvRecordingLocator.Find([overMidnight], At(5, 0, 40)));
        Assert.Null(TvRecordingLocator.Find([overMidnight], At(5, 1, 0, 1)));
    }

    [Fact]
    public void ClipOffsets_measures_the_window_from_the_start_of_the_recording()
    {
        var recording = Candidate(At(5, 12, 40, 38));

        var (start, end) = TvRecordingLocator.ClipOffsets(recording, At(5, 12, 50), At(5, 12, 52));

        Assert.Equal(562, start); // 12:50:00 - 12:40:38
        Assert.Equal(682, end);
    }

    [Fact]
    public void ClipOffsets_clamps_the_window_to_the_recording_limits()
    {
        var recording = Candidate(At(5, 12, 5, 59)); // runs until 13:00:00

        var (start, end) = TvRecordingLocator.ClipOffsets(recording, At(5, 12, 0), At(5, 13, 10));

        Assert.Equal(0, start);
        Assert.Equal(3241, end); // 13:00:00 - 12:05:59
    }
}
