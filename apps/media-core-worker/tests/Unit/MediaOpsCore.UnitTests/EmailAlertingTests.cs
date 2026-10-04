using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Modules.Alerting.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class AlertRecipientsTests
{
    [Theory]
    [InlineData("ana@entidad.gov.co", true)]
    [InlineData(" ana@entidad.gov.co ", true)]
    [InlineData("573001234567", false)]
    [InlineData("+573001234567", false)]
    [InlineData("120363030421643321@g.us", false)]
    [InlineData("573001234567@s.whatsapp.net", false)]
    [InlineData("sin-arroba.com", false)]
    [InlineData("a@b", false)]
    [InlineData("", false)]
    public void IsEmail_tells_an_email_apart_from_phone_numbers_and_whatsapp_ids(string recipient, bool expected)
    {
        Assert.Equal(expected, AlertRecipients.IsEmail(recipient));
    }
}

public sealed class AlertEmailMessageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_puts_level_client_and_outlet_in_the_subject_and_plain_text_in_the_body()
    {
        var alert = new Alert(
            "El barril subio.", At, At, "internet", "El Tiempo", string.Empty,
            ["petroleo"], "Ecopetrol", AlertType.New, title: "Sube el petroleo", url: "https://x.co/a", level: AlertLevel.Risk);

        var message = AlertEmailMessage.Build(alert);

        Assert.Equal("[N3] Ecopetrol — El Tiempo", message.Subject);
        Assert.DoesNotContain('*', message.Body);
        Assert.Contains("Sube el petroleo", message.Body);
        Assert.Contains("https://x.co/a", message.Body);
    }

    [Fact]
    public void Build_leaves_the_level_out_of_an_informative_subject()
    {
        var alert = new Alert("se derramo petroleo", At, At.AddSeconds(20), "tv", "CaracolTV", "f.opus", ["petroleo"], "Ecopetrol", AlertType.New);

        Assert.Equal("Ecopetrol — CaracolTV", AlertEmailMessage.Build(alert).Subject);
    }
}

public sealed class RoutingAlertNotifierTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private static Alert SampleAlert() => new("texto", At, At, "tv", "CaracolTV", "f.opus", ["petroleo"], "Ecopetrol", AlertType.New);

    private sealed class RecordingNotifier : IAlertNotifier
    {
        public List<string> Received { get; } = [];
        public Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            Received.AddRange(recipients);
            return Task.FromResult(recipients);
        }
    }

    [Fact]
    public async Task NotifyAsync_sends_emails_through_the_email_notifier_and_everything_else_through_whatsapp()
    {
        var whatsApp = new RecordingNotifier();
        var email = new RecordingNotifier();
        var sut = new RoutingAlertNotifier(whatsApp, email, NullLogger<RoutingAlertNotifier>.Instance);

        var succeeded = await sut.NotifyAsync(SampleAlert(), ["573001234567", "ana@entidad.gov.co", "120363030421643321@g.us"]);

        Assert.Equal(["573001234567", "120363030421643321@g.us"], whatsApp.Received);
        Assert.Equal(["ana@entidad.gov.co"], email.Received);
        Assert.Equal(3, succeeded.Count);
    }

    [Fact]
    public async Task NotifyAsync_skips_emails_when_no_email_channel_is_configured_instead_of_texting_them()
    {
        var whatsApp = new RecordingNotifier();
        var sut = new RoutingAlertNotifier(whatsApp, null, NullLogger<RoutingAlertNotifier>.Instance);

        var succeeded = await sut.NotifyAsync(SampleAlert(), ["573001234567", "ana@entidad.gov.co"]);

        Assert.Equal(["573001234567"], whatsApp.Received);
        Assert.Equal(["573001234567"], succeeded);
    }
}
