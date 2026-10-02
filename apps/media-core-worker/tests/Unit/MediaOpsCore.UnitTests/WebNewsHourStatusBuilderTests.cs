using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class WebNewsHourStatusBuilderTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static SourceIncident At(int minute, string kind) =>
        new(new DateTimeOffset(2026, 10, 2, 10, minute, 0, Bogota), "google-news", kind, null);

    [Fact]
    public void Build_reports_no_data_when_no_cycle_ran_this_hour()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([], Bogota);

        Assert.Equal("no-data", status);
        Assert.Null(segments);
    }

    [Fact]
    public void Build_reports_ok_when_both_half_hour_cycles_completed_cleanly()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build(
            [At(5, IncidentKinds.CycleOk), At(35, IncidentKinds.CycleOk)], Bogota);

        Assert.Equal("ok", status);
        Assert.Equal(2, segments!.Count);
        Assert.All(segments, s => Assert.Equal("captured", s.State));
    }

    [Fact]
    public void Build_reports_no_session_when_both_halves_failed_or_never_ran()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([At(5, IncidentKinds.CycleFailed)], Bogota);

        Assert.Equal("no-session", status);
        Assert.All(segments!, s => Assert.Equal("no-session", s.State));
    }

    [Fact]
    public void Build_reports_gap_filled_when_one_half_ran_and_the_other_did_not()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build([At(5, IncidentKinds.CycleOk)], Bogota);

        Assert.Equal("gap-filled", status);
        Assert.NotNull(segments);
        var segmentList = segments!;
        Assert.Equal("captured", segmentList.Single(s => s.StartSeconds == 0).State);
        Assert.Equal("no-session", segmentList.Single(s => s.StartSeconds == 1800).State);
    }

    [Fact]
    public void Build_marks_a_clean_cycle_with_article_failures_as_a_gap_not_fully_captured()
    {
        var (status, segments) = WebNewsHourStatusBuilder.Build(
            [At(5, IncidentKinds.CycleOk), At(6, IncidentKinds.ArticleFailed), At(35, IncidentKinds.CycleOk)], Bogota);

        Assert.Equal("gap-filled", status);
        Assert.NotNull(segments);
        var segmentList = segments!;
        Assert.Equal("gap", segmentList.Single(s => s.StartSeconds == 0).State);
        Assert.Equal("captured", segmentList.Single(s => s.StartSeconds == 1800).State);
    }

    [Fact]
    public void Build_marks_a_clean_cycle_with_a_partial_send_as_a_gap()
    {
        var (status, _) = WebNewsHourStatusBuilder.Build([At(5, IncidentKinds.CycleOk), At(6, IncidentKinds.SendFailed)], Bogota);

        Assert.Equal("gap-filled", status);
    }
}
