using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class RetentionPurgeTests
{
    private static string MakeDayDir(string root, string media, DateOnly date, string sourceId)
    {
        var dir = Path.Combine(root, media, date.Year.ToString("D4"), date.Month.ToString("D2"), date.Day.ToString("D2"), sourceId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{sourceId}_{date:yyyy-MM-dd}_10-00-00.opus"), "fake");
        return Path.GetDirectoryName(dir)!; // the dd folder, one level above the sourceId folder
    }

    [Fact]
    public void FindDayFoldersOlderThan_returns_only_folders_strictly_before_the_cutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}");
        try
        {
            var oldDay = MakeDayDir(root, "radio", new DateOnly(2026, 10, 1), "wradio");
            var cutoffDay = MakeDayDir(root, "radio", new DateOnly(2026, 10, 2), "wradio");
            var recentDay = MakeDayDir(root, "television", new DateOnly(2026, 10, 3), "noticias-caracol-live");

            var result = RetentionPurge.FindDayFoldersOlderThan(root, new DateOnly(2026, 10, 2));

            Assert.Equal([oldDay], result);
            Assert.DoesNotContain(cutoffDay, result);
            Assert.DoesNotContain(recentDay, result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindDayFoldersOlderThan_ignores_directories_that_are_not_shaped_like_yyyy_MM_dd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Games", "SomeGame", "Saves"));
            Directory.CreateDirectory(Path.Combine(root, "$RECYCLE.BIN", "S-1-5"));
            MakeDayDir(root, "radio", new DateOnly(2000, 1, 1), "wradio");

            var result = RetentionPurge.FindDayFoldersOlderThan(root, new DateOnly(2026, 1, 1));

            Assert.Single(result);
            Assert.Contains("radio", result[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindDayFoldersOlderThan_returns_nothing_for_a_missing_root()
    {
        var result = RetentionPurge.FindDayFoldersOlderThan(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), new DateOnly(2026, 1, 1));

        Assert.Empty(result);
    }
}

public sealed class DiskSpaceAlertTests
{
    [Theory]
    [InlineData(false, 5.0, 10, DiskSpaceTransition.BecameLow)]
    [InlineData(true, 5.0, 10, DiskSpaceTransition.StillLow)]
    [InlineData(false, 20.0, 10, DiskSpaceTransition.None)]
    // Hysteresis: recovery needs more headroom than the raw threshold, so it doesn't flap right at the line.
    [InlineData(true, 10.5, 10, DiskSpaceTransition.StillLow)]
    [InlineData(true, 12.5, 10, DiskSpaceTransition.Recovered)]
    public void Evaluate_tracks_low_disk_transitions_with_hysteresis(bool wasLow, double freeGb, int minFreeDiskGb, DiskSpaceTransition expected)
    {
        Assert.Equal(expected, DiskSpaceAlert.Evaluate(wasLow, freeGb, minFreeDiskGb));
    }
}
