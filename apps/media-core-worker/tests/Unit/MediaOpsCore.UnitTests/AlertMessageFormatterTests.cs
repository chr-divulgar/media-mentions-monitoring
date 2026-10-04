using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class AlertMessageFormatterTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 3, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Format_returns_null_for_repeated_within_minute()
    {
        var alert = new Alert("texto", At, At, "tv", "CaracolTV", "file.opus", ["petroleo"], "Ecopetrol", AlertType.RepeatedWithinMinute);

        Assert.Null(AlertMessageFormatter.Format(alert));
    }

    [Fact]
    public void Format_builds_the_audio_message_with_the_time_range()
    {
        var alert = new Alert("se derramo petroleo en el pozo", At, At.AddSeconds(20), "tv", "CaracolTV", "file.opus", ["petroleo"], "Ecopetrol", AlertType.New);

        var message = AlertMessageFormatter.Format(alert);

        Assert.Contains("*CaracolTV*", message);
        Assert.Contains("(11:00:00_11:00:20)", message);
    }

    [Fact]
    public void Format_builds_a_web_article_message_without_a_time_range()
    {
        var alert = new Alert(
            "El barril subio esta semana.", At, At, "internet", "El Tiempo", string.Empty,
            ["petroleo"], "Ecopetrol", AlertType.New, title: "Sube el petroleo", url: "https://eltiempo.com/nota");

        var message = AlertMessageFormatter.Format(alert);

        Assert.Equal(
            "*Petroleo*\n*El Tiempo*\n*Sube el petroleo*\nEl barril subio esta semana.\nhttps://eltiempo.com/nota",
            message);
    }

    [Fact]
    public void Format_leaves_an_informative_audio_alert_unchanged()
    {
        var alert = new Alert("se derramo petroleo en el pozo", At, At.AddSeconds(20), "tv", "CaracolTV", "file.opus", ["petroleo"], "Ecopetrol", AlertType.New);

        Assert.StartsWith("*Petroleo*", AlertMessageFormatter.Format(alert));
    }

    [Theory]
    [InlineData(AlertLevel.FollowUp, "*SEGUIMIENTO (N2)*")]
    [InlineData(AlertLevel.Risk, "*RIESGO (N3)*")]
    [InlineData(AlertLevel.Crisis, "*CRISIS (N4)*")]
    public void Format_puts_the_level_banner_first_for_n2_and_above(AlertLevel level, string banner)
    {
        var alert = new Alert(
            "se derramo petroleo en el pozo", At, At.AddSeconds(20), "tv", "CaracolTV", "file.opus",
            ["petroleo"], "Ecopetrol", AlertType.New, level: level);

        Assert.StartsWith(banner + "\n*Petroleo*", AlertMessageFormatter.Format(alert));
    }

    [Fact]
    public void Format_puts_the_level_banner_first_on_web_articles_too()
    {
        var alert = new Alert(
            "texto", At, At, "internet", "El Tiempo", string.Empty,
            ["petroleo"], "Ecopetrol", AlertType.New, title: "Titulo", url: "https://x.co/a", level: AlertLevel.Risk);

        Assert.StartsWith("*RIESGO (N3)*\n*Petroleo*", AlertMessageFormatter.Format(alert));
    }
}

public sealed class AlertLevelTests
{
    [Theory]
    [InlineData("N1", AlertLevel.Informative)]
    [InlineData("n2", AlertLevel.FollowUp)]
    [InlineData("N3", AlertLevel.Risk)]
    [InlineData(" N4 ", AlertLevel.Crisis)]
    [InlineData("", AlertLevel.Informative)]
    [InlineData(null, AlertLevel.Informative)]
    [InlineData("N9", AlertLevel.Informative)]
    public void Parse_reads_the_label_and_falls_back_to_informative(string? label, AlertLevel expected)
    {
        Assert.Equal(expected, AlertLevelExtensions.Parse(label));
    }

    [Fact]
    public void ToLabel_round_trips_every_level()
    {
        foreach (var level in Enum.GetValues<AlertLevel>())
        {
            Assert.Equal(level, AlertLevelExtensions.Parse(level.ToLabel()));
        }
    }
}
