using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Which radio hour files the one-time conversion to 32 kbps takes: closed, big enough to still be at the old bitrate,
// not already converted, and never the hour being recorded.
public sealed class RadioOpusConversionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 23, 18, 30, 0, DateTimeKind.Utc); // 13:30 in Bogota
    private const long Big = 28_000_000;
    private const long Small = 14_000_000;
    private const long MinBytes = 21_000_000;

    private readonly string root = Path.Combine(Path.GetTempPath(), "radio-conv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // radio/2026/11/10/SourceA/SourceA_2026-11-10_10-00-00.opus
    private string Write(string medium, string day, string source, string hour, long size, string extension = "opus", DateTime? lastWriteUtc = null)
    {
        var date = DateTime.Parse(day);
        var directory = Path.Combine(root, medium, $"{date:yyyy}", $"{date:MM}", $"{date:dd}", source);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{source}_{day}_{hour}.{extension}");
        using (var stream = File.Create(path))
        {
            stream.SetLength(size);
        }

        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? Now.AddHours(-30));
        return path;
    }

    private IReadOnlyList<string> Find(Func<string, long, bool>? isConverted = null) =>
        RadioOpusConversion
            .FindCandidates(root, isConverted ?? ((_, _) => false), Now, TimeSpan.FromMinutes(20), null, MinBytes)
            .Select(candidate => Path.GetFileName(candidate.Path))
            .ToArray();

    [Fact]
    public void Takes_the_closed_radio_hours_that_are_still_at_the_old_bitrate_newest_first()
    {
        Write("radio", "2026-11-10", "A", "10-00-00", Big, lastWriteUtc: Now.AddDays(-13));
        Write("radio", "2026-11-20", "A", "09-00-00", Big, lastWriteUtc: Now.AddDays(-3));
        Write("radio", "2026-11-15", "B", "23-00-00", Big, lastWriteUtc: Now.AddDays(-8));

        Assert.Equal(["A_2026-11-20_09-00-00.opus", "B_2026-11-15_23-00-00.opus", "A_2026-11-10_10-00-00.opus"], Find());
    }

    [Fact]
    public void Leaves_alone_what_is_already_small_enough()
    {
        Write("radio", "2026-11-22", "A", "10-00-00", Small);
        Write("radio", "2026-11-22", "A", "11-00-00", Big);

        Assert.Equal(["A_2026-11-22_11-00-00.opus"], Find());
    }

    [Fact]
    public void Never_takes_television_or_anything_that_is_not_an_opus()
    {
        Write("television", "2026-11-22", "Canal", "10-00-00", Big);
        Write("television", "2026-11-22", "Canal", "11-00-00", Big, "mp4");
        Write("radio", "2026-11-22", "A", "10-00-00", 60_000, "json");
        Write("radio", "2026-11-22", "A", "11-00-00", Big, "opus.convert.tmp");

        Assert.Empty(Find());
    }

    [Fact]
    public void Never_takes_the_hour_that_is_still_being_recorded_or_a_file_written_a_moment_ago()
    {
        Write("radio", "2026-11-23", "A", "13-00-00", Big, lastWriteUtc: Now.AddMinutes(-1));   // the hour in progress (13:00 Bogota)
        Write("radio", "2026-11-23", "A", "12-00-00", Big, lastWriteUtc: Now.AddMinutes(-5));   // closed, but written 5 min ago
        Write("radio", "2026-11-23", "B", "12-00-00", Big, lastWriteUtc: Now.AddHours(-1));     // closed and idle

        Assert.Equal(["B_2026-11-23_12-00-00.opus"], Find());
    }

    [Fact]
    public void Skips_what_the_converted_ledger_already_has_with_that_size()
    {
        Write("radio", "2026-11-20", "A", "09-00-00", Big);
        Write("radio", "2026-11-20", "A", "10-00-00", Big);

        var found = Find((relative, size) => relative.EndsWith("09-00-00.opus") && size == Big);

        Assert.Equal(["A_2026-11-20_10-00-00.opus"], found);
    }

    [Fact]
    public void The_candidate_carries_its_path_relative_to_the_root_with_forward_slashes_and_its_size()
    {
        Write("radio", "2026-11-20", "A", "09-00-00", Big);

        var candidate = Assert.Single(RadioOpusConversion.FindCandidates(root, (_, _) => false, Now, TimeSpan.FromMinutes(20), null, MinBytes));

        Assert.Equal("radio/2026/11/20/A/A_2026-11-20_09-00-00.opus", candidate.RelativePath);
        Assert.Equal(Big, candidate.Size);
    }

    [Fact]
    public void The_recent_hours_average_shows_what_size_the_capture_is_writing_now()
    {
        Write("radio", "2026-11-23", "A", "10-00-00", 14_000_000, lastWriteUtc: Now.AddHours(-2));
        Write("radio", "2026-11-23", "A", "11-00-00", 14_400_000, lastWriteUtc: Now.AddHours(-1));
        Write("radio", "2026-11-23", "A", "12-00-00", 14_800_000, lastWriteUtc: Now.AddMinutes(-40));

        var average = RadioOpusConversion.RecentClosedHoursAverageMb(root, Now, TimeSpan.FromMinutes(20), null, count: 24);

        Assert.NotNull(average);
        Assert.Equal(14.4, average.Value, precision: 1);
    }

    [Fact]
    public void There_is_no_recent_average_without_closed_hours()
    {
        Assert.Null(RadioOpusConversion.RecentClosedHoursAverageMb(root, Now, TimeSpan.FromMinutes(20), null, count: 24));
    }
}
