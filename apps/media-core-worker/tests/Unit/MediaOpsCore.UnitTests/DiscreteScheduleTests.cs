using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The search cycles run on the clock (:00, :15, :30, :45), not "interval after the last one ended": otherwise the
// period drifts by the length of each cycle and a quarter of an hour of the status grid can end up with no cycle.
public sealed class DiscreteScheduleTests
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 22, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void A_cycle_that_took_three_minutes_is_followed_by_the_next_mark()
    {
        // Started on the mark at 10:00, finished at 10:03: next one at 10:15.
        Assert.Equal(TimeSpan.FromMinutes(12), DiscreteSchedule.DelayUntilNextMark(At(10, 3), Quarter));
    }

    [Fact]
    public void A_cycle_that_overran_a_mark_does_not_pile_up_the_missed_one()
    {
        // Started at 10:00, took 20 minutes: 10:15 is gone, the next mark is 10:30 (not an immediate cycle for 10:15).
        Assert.Equal(TimeSpan.FromMinutes(10), DiscreteSchedule.DelayUntilNextMark(At(10, 20), Quarter));
    }

    [Fact]
    public void Right_on_a_mark_waits_a_whole_interval()
    {
        Assert.Equal(Quarter, DiscreteSchedule.DelayUntilNextMark(At(10, 15), Quarter));
    }

    [Fact]
    public void Marks_follow_the_hour_for_any_interval_that_divides_it()
    {
        Assert.Equal(TimeSpan.FromMinutes(25), DiscreteSchedule.DelayUntilNextMark(At(10, 5), TimeSpan.FromMinutes(30)));
        Assert.Equal(TimeSpan.FromSeconds(50), DiscreteSchedule.DelayUntilNextMark(At(10, 9, 10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void An_interval_of_a_second_or_less_just_waits_that_interval()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), DiscreteSchedule.DelayUntilNextMark(At(10, 0), TimeSpan.FromMilliseconds(500)));
    }
}
