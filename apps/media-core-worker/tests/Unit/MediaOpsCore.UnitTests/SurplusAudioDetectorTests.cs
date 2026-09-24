using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class SurplusAudioDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 11, 0, 0, TimeSpan.FromHours(-5));

    private static SurplusAudioDetector NewDetector() => new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));

    [Fact]
    public void Occasional_small_surplus_is_tolerated()
    {
        var detector = NewDetector();
        Assert.False(detector.RecordSurplus(T0, TimeSpan.FromSeconds(10)));
        Assert.False(detector.RecordSurplus(T0.AddMinutes(5), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Stream_repeating_audio_trips_within_minutes()
    {
        // Meridiano_70: ~0.25s of surplus per second of wall clock.
        var detector = NewDetector();
        var trippedAtSecond = -1;
        for (var second = 0; second < 600 && trippedAtSecond < 0; second++)
        {
            if (detector.RecordSurplus(T0.AddSeconds(second), TimeSpan.FromMilliseconds(250)))
            {
                trippedAtSecond = second;
            }
        }

        Assert.InRange(trippedAtSecond, 100, 130);
    }

    [Fact]
    public void Surplus_from_a_previous_window_is_forgotten()
    {
        var detector = NewDetector();
        Assert.False(detector.RecordSurplus(T0, TimeSpan.FromSeconds(20)));
        Assert.False(detector.RecordSurplus(T0.AddMinutes(10), TimeSpan.FromSeconds(20)));
    }
}
