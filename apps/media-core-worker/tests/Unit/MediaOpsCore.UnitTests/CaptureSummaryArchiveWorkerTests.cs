using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class CaptureSummaryArchiveWorkerTests
{
    private const string HourKey = "2026-01-01_10";
    private static readonly DateTimeOffset HourStart = new(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void CanArchiveHour_should_wait_while_a_session_is_still_recording_that_hour()
    {
        // The clock has moved on but this source hasn't rotated yet — archiving now would freeze
        // its last minutes in Firestore as uncovered.
        var canArchive = CaptureSummaryArchiveWorker.CanArchiveHour(HourKey, [HourStart], TimeSpan.Zero);

        Assert.False(canArchive);
    }

    [Fact]
    public void CanArchiveHour_should_archive_once_every_session_has_rotated_past_that_hour()
    {
        var canArchive = CaptureSummaryArchiveWorker.CanArchiveHour(
            HourKey, [HourStart.AddHours(1), HourStart.AddHours(1)], TimeSpan.Zero);

        Assert.True(canArchive);
    }

    [Fact]
    public void CanArchiveHour_should_archive_when_nothing_is_recording_at_all()
    {
        var canArchive = CaptureSummaryArchiveWorker.CanArchiveHour(HourKey, [], TimeSpan.Zero);

        Assert.True(canArchive);
    }

    [Fact]
    public void CanArchiveHour_should_stop_waiting_on_a_session_that_never_rotates()
    {
        // A stalled session keeps reporting the window it froze in forever — without the ceiling
        // one frozen source would keep the hour out of Firestore indefinitely.
        var canArchive = CaptureSummaryArchiveWorker.CanArchiveHour(HourKey, [HourStart], TimeSpan.FromMinutes(20));

        Assert.True(canArchive);
    }
}
