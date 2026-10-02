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
}
