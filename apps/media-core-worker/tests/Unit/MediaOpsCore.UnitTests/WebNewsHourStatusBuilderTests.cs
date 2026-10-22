using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// A search cycle runs every 15 minutes, so an hour of the "internet" row is four 15-minute segments.
public sealed class WebNewsHourStatusBuilderTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static SourceIncident At(int minute, string kind) =>
        new(new DateTimeOffset(2026, 10, 2, 10, minute, 0, Bogota), "google-news", kind, null);

    private static IEnumerable<SourceIncident> CleanCycles(params int[] minutes) => minutes.Select(minute => At(minute, IncidentKinds.CycleOk));

    [Fact]
    public void Build_reports_no_data_when_no_cycle_ran_this_hour()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([], Bogota);

        Assert.Equal("no-data", status);
        Assert.Null(segments);
    }

    [Fact]
    public void Build_reports_ok_when_all_four_quarter_hour_cycles_completed_cleanly()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([.. CleanCycles(2, 17, 32, 47)], Bogota);

        Assert.Equal("ok", status);
        Assert.Equal([0d, 900d, 1800d, 2700d], segments!.Select(s => s.StartSeconds));
        Assert.Equal([900d, 1800d, 2700d, 3600d], segments!.Select(s => s.EndSeconds));
        Assert.All(segments!, s => Assert.Equal("captured", s.State));
    }

    [Fact]
    public void Build_reports_no_session_when_every_quarter_failed_or_never_ran()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([At(5, IncidentKinds.CycleFailed)], Bogota);

        Assert.Equal("no-session", status);
        Assert.Equal(4, segments!.Count);
        Assert.All(segments!, s => Assert.Equal("no-session", s.State));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(20, 900)]
    [InlineData(35, 1800)]
    [InlineData(50, 2700)]
    public void Build_marks_only_the_quarter_a_cycle_ran_in(int minute, double expectedStart)
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([At(minute, IncidentKinds.CycleOk)], Bogota);

        Assert.Equal("gap-filled", status);
        Assert.Equal([expectedStart], segments!.Where(s => s.State == "captured").Select(s => s.StartSeconds));
        Assert.Equal(3, segments!.Count(s => s.State == "no-session"));
    }

    [Fact]
    public void Build_puts_the_quarter_boundary_minutes_in_the_quarter_that_starts_there()
    {
        var (_, segments) = WebNewsHourStatusBuilder.Build([At(14, IncidentKinds.CycleOk), At(15, IncidentKinds.CycleOk)], Bogota);

        Assert.Equal([0d, 900d], segments!.Where(s => s.State == "captured").Select(s => s.StartSeconds));
    }

    [Fact]
    public void Build_marks_a_clean_cycle_with_article_failures_as_a_gap_not_fully_captured()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build(
            [At(5, IncidentKinds.CycleOk), At(6, IncidentKinds.ArticleFailed), At(20, IncidentKinds.CycleOk), At(35, IncidentKinds.CycleOk), At(50, IncidentKinds.CycleOk)],
            Bogota);

        Assert.Equal("gap-filled", status);
        Assert.Equal("gap", segments!.Single(s => s.StartSeconds == 0).State);
        Assert.Equal("captured", segments!.Single(s => s.StartSeconds == 900).State);
    }

    [Fact]
    public void Build_marks_a_clean_cycle_with_a_partial_send_as_a_gap()
    {
        var (status, _) = WebNewsHourStatusBuilder.Build([At(5, IncidentKinds.CycleOk), At(6, IncidentKinds.SendFailed)], Bogota);

        Assert.Equal("gap-filled", status);
    }
}
