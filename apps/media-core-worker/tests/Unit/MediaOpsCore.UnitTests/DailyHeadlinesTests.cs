using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class DailyHeadlinesScheduleTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    [Theory]
    [InlineData(6, 59, false)]
    [InlineData(7, 0, true)]
    [InlineData(13, 30, true)]
    public void IsDue_only_from_the_configured_bogota_time_onwards(int hour, int minute, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 3, hour, minute, 0, Bogota);

        Assert.Equal(expected, DailyHeadlinesSchedule.IsDue(now, new TimeOnly(7, 0), alreadySentToday: false));
    }

    [Fact]
    public void IsDue_is_false_once_todays_digest_was_sent()
    {
        var now = new DateTimeOffset(2026, 10, 3, 9, 0, 0, Bogota);

        Assert.False(DailyHeadlinesSchedule.IsDue(now, new TimeOnly(7, 0), alreadySentToday: true));
    }

    [Fact]
    public void IsDue_reads_the_clock_in_bogota_even_when_given_utc()
    {
        // 11:30 UTC is 06:30 in Bogotá: not yet 07:00 there.
        var now = new DateTimeOffset(2026, 10, 3, 11, 30, 0, TimeSpan.Zero);

        Assert.False(DailyHeadlinesSchedule.IsDue(now, new TimeOnly(7, 0), alreadySentToday: false));
        Assert.Equal(new DateOnly(2026, 10, 3), DailyHeadlinesSchedule.BogotaDay(now));
    }

    [Fact]
    public void BogotaDay_is_still_the_previous_day_just_after_utc_midnight()
    {
        var now = new DateTimeOffset(2026, 10, 4, 2, 0, 0, TimeSpan.Zero); // 21:00 on the 3rd in Bogotá

        Assert.Equal(new DateOnly(2026, 10, 3), DailyHeadlinesSchedule.BogotaDay(now));
    }
}

public sealed class DailyHeadlinesDigestUseCaseTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);
    private static readonly DateOnly Day = new(2026, 10, 3);

    private static Alert WebAlert(string client, string title, string outlet, DateTimeOffset at, AlertLevel level = AlertLevel.Informative) =>
        new("primer parrafo", at, at, "internet", outlet, string.Empty, ["petroleo"], client, AlertType.New,
            title: title, url: $"https://{outlet.ToLowerInvariant().Replace(" ", string.Empty)}.co/{title.Length}", level: level);

    private static ClientKeywordConfig Client(string name, string[]? phones = null, string[]? emails = null) =>
        new(
            name,
            [new KeywordConfig("petroleo", [])],
            phones is null
                ? new Dictionary<string, IReadOnlyList<string>>()
                : new Dictionary<string, IReadOnlyList<string>> { ["internet"] = phones },
            emails is null
                ? null
                : new Dictionary<string, IReadOnlyList<string>> { ["internet"] = emails });

    [Fact]
    public async Task ExecuteAsync_sends_each_client_its_days_headlines_with_the_most_severe_first_in_the_top()
    {
        var alerts = new FakeAlerts(
            WebAlert("Ecopetrol", "Nota tranquila", "El Tiempo", new DateTimeOffset(2026, 10, 3, 9, 0, 0, Bogota)),
            WebAlert("Ecopetrol", "Derrame en el pozo", "Semana", new DateTimeOffset(2026, 10, 3, 6, 0, 0, Bogota), AlertLevel.Crisis),
            WebAlert("Ecopetrol", "Otra nota", "La Republica", new DateTimeOffset(2026, 10, 3, 12, 0, 0, Bogota)));
        var notifier = new RecordingMessageNotifier();
        var sut = new DailyHeadlinesDigestUseCase(
            new FakeClients(Client("Ecopetrol", phones: ["573001112233"], emails: ["ana@entidad.gov.co"])), alerts, notifier);

        var clientsNotified = await sut.ExecuteAsync(Day);

        Assert.Equal(1, clientsNotified);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(["573001112233", "ana@entidad.gov.co"], sent.Recipients);
        Assert.Contains("Titulares del día — Ecopetrol — 03/10/2026", sent.Text);
        Assert.Contains("Todos los titulares (3)", sent.Text);
        // The crisis story leads the top even though it is the oldest.
        Assert.True(
            sent.Text.IndexOf("Derrame en el pozo", StringComparison.Ordinal) < sent.Text.IndexOf("Nota tranquila", StringComparison.Ordinal));
        Assert.Contains("[N4] Derrame en el pozo — Semana", sent.Text);
    }

    [Fact]
    public async Task ExecuteAsync_asks_the_repository_for_exactly_the_bogota_day()
    {
        var alerts = new FakeAlerts();
        var sut = new DailyHeadlinesDigestUseCase(new FakeClients(Client("Ecopetrol", phones: ["573001112233"])), alerts, new RecordingMessageNotifier());

        await sut.ExecuteAsync(Day);

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, Bogota), alerts.RequestedFrom);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, Bogota), alerts.RequestedTo);
        Assert.Equal("internet", alerts.RequestedMedia);
    }

    [Fact]
    public async Task ExecuteAsync_skips_clients_without_alerts_that_day_or_without_recipients()
    {
        var alerts = new FakeAlerts(
            WebAlert("Sin destinatarios", "Nota", "El Tiempo", new DateTimeOffset(2026, 10, 3, 9, 0, 0, Bogota)));
        var notifier = new RecordingMessageNotifier();
        var sut = new DailyHeadlinesDigestUseCase(
            new FakeClients(Client("Sin destinatarios"), Client("Sin notas", phones: ["573001112233"])), alerts, notifier);

        Assert.Equal(0, await sut.ExecuteAsync(Day));
        Assert.Empty(notifier.Sent);
    }

    private sealed class FakeClients(params ClientKeywordConfig[] clients) : IClientConfigRepository
    {
        public Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientKeywordConfig>>(clients);

        public Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAlerts(params Alert[] alerts) : IDailyHeadlinesSource
    {
        public DateTimeOffset RequestedFrom { get; private set; }
        public DateTimeOffset RequestedTo { get; private set; }
        public string? RequestedMedia { get; private set; }

        public Task<IReadOnlyList<Alert>> GetAlertsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string media, CancellationToken cancellationToken = default)
        {
            RequestedFrom = fromUtc;
            RequestedTo = toUtc;
            RequestedMedia = media;
            return Task.FromResult<IReadOnlyList<Alert>>(alerts.ToArray());
        }
    }

    private sealed class RecordingMessageNotifier : IMessageNotifier
    {
        public List<(string Text, IReadOnlyList<string> Recipients)> Sent { get; } = [];

        public Task<IReadOnlyList<string>> SendAsync(string text, IReadOnlyList<string> recipients, string? subject = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((text, recipients));
            return Task.FromResult(recipients);
        }
    }
}
