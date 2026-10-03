using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class DetectAlertsUseCaseTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 3, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_ignores_text_with_no_keyword_match()
    {
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "hoy no llovió nada en la ciudad", BaseTime, BaseTime.AddSeconds(20));

        Assert.Empty(repository.Inserted);
    }

    [Fact]
    public async Task ExecuteAsync_classifies_as_new_by_default()
    {
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));

        var alert = Assert.Single(repository.Inserted);
        Assert.Equal(AlertType.New, alert.Type);
        Assert.Equal(new[] { "petroleo" }, alert.Words);
    }

    [Fact]
    public async Task ExecuteAsync_classifies_as_ad_when_keyword_is_surrounded_by_configured_ad_context()
    {
        var repository = new InMemoryAlertRepository();
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[]
            {
                new KeywordConfig("petroleo", new[] { new KeywordAdContext("mensaje comercial", string.Empty) })
            },
            new Dictionary<string, IReadOnlyList<string>>());
        var useCase = CreateUseCase(repository, client);

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "este es un mensaje comercial de petroleo colombiano", BaseTime, BaseTime.AddSeconds(20));

        var alert = Assert.Single(repository.Inserted);
        Assert.Equal(AlertType.Ad, alert.Type);
    }

    [Fact]
    public async Task ExecuteAsync_classifies_as_repeated_within_minute_when_last_own_platform_alert_ended_within_60_seconds()
    {
        var repository = new InMemoryAlertRepository();
        repository.Seed(new Alert(
            "primer aviso de petroleo",
            BaseTime,
            BaseTime.AddSeconds(10),
            "tv",
            "CaracolTV",
            "prev.opus",
            new[] { "petroleo" },
            "Ecopetrol",
            AlertType.New));
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "segundo aviso de petroleo", BaseTime.AddSeconds(40), BaseTime.AddSeconds(60));

        var alert = repository.Inserted.Single(a => a.FilePath == "file.opus");
        Assert.Equal(AlertType.RepeatedWithinMinute, alert.Type);
    }

    [Fact]
    public async Task ExecuteAsync_classifies_as_repeated_other_platform_when_context_matches_another_platforms_last_alert_within_a_minute()
    {
        var repository = new InMemoryAlertRepository();
        repository.Seed(new Alert(
            "hola buenas tardes hablamos de petroleo en la region hoy",
            BaseTime.AddSeconds(-40),
            BaseTime.AddSeconds(-20),
            "tv",
            "RCN",
            "other.opus",
            new[] { "petroleo" },
            "Ecopetrol",
            AlertType.New));
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"), "CaracolTV", "RCN");

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "hola buenas tardes hablamos de petroleo en otra emisora", BaseTime, BaseTime.AddSeconds(20));

        var alert = repository.Inserted.Single(a => a.FilePath == "file.opus");
        Assert.Equal(AlertType.RepeatedOtherPlatform, alert.Type);
    }

    [Fact]
    public async Task ExecuteAsync_classifies_as_new_when_matching_other_platform_alert_is_older_than_a_minute()
    {
        var repository = new InMemoryAlertRepository();
        repository.Seed(new Alert(
            "hola buenas tardes hablamos de petroleo en la region hoy",
            BaseTime.AddMinutes(-10),
            BaseTime.AddMinutes(-10).AddSeconds(20),
            "tv",
            "RCN",
            "other.opus",
            new[] { "petroleo" },
            "Ecopetrol",
            AlertType.New));
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"), "CaracolTV", "RCN");

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "hola buenas tardes hablamos de petroleo en otra emisora", BaseTime, BaseTime.AddSeconds(20));

        var alert = repository.Inserted.Single(a => a.FilePath == "file.opus");
        Assert.Equal(AlertType.New, alert.Type);
    }

    [Fact]
    public async Task ExecuteAsync_resolves_television_media_against_recipients_configured_under_tv()
    {
        // CaptureSource.Media says "television" (FirestoreDocumentMapper.NormalizeMedia), but every
        // client's recipients are keyed "tv" — without translating back, TV alerts matched nobody.
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier();
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>> { ["tv"] = new[] { "573000000000" } });
        var useCase = CreateUseCase(repository, client, notifier: notifier);

        await useCase.ExecuteAsync("CaracolTV", "television", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));

        var alert = Assert.Single(repository.Inserted);
        Assert.Equal("tv", alert.Media);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal(new[] { "573000000000" }, call.Recipients);
    }

    [Fact]
    public async Task ExecuteAsync_notifies_recipients_configured_for_the_alert_media()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier();
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>> { ["tv"] = new[] { "573000000000" } });
        var useCase = CreateUseCase(repository, client, notifier: notifier);

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));

        var call = Assert.Single(notifier.Calls);
        Assert.Equal(new[] { "573000000000" }, call.Recipients);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_notify_when_no_recipients_configured_for_the_media()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"), notifier: notifier);

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_only_marks_recipients_that_the_notifier_confirmed_as_notified()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier { RecipientSucceeds = number => number == "573000000000" };
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>> { ["tv"] = new[] { "573000000000", "573111111111" } });
        var useCase = CreateUseCase(repository, client, notifier: notifier);

        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));

        var pending = await repository.GetPendingNotificationsAsync(TimeSpan.FromDays(1));
        var item = Assert.Single(pending);
        Assert.Equal(new[] { "573111111111" }, item.PendingRecipients);
    }

    [Fact]
    public async Task RetryPendingNotificationsAsync_resends_only_to_recipients_still_pending()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier { RecipientSucceeds = number => number == "573000000000" };
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>> { ["tv"] = new[] { "573000000000", "573111111111" } });
        var useCase = CreateUseCase(repository, client, notifier: notifier);
        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));
        notifier.Calls.Clear();

        // Second failed recipient now succeeds (e.g. the WhatsApp sidecar recovered).
        notifier.RecipientSucceeds = _ => true;
        await useCase.RetryPendingNotificationsAsync();

        var retryCall = Assert.Single(notifier.Calls);
        Assert.Equal(new[] { "573111111111" }, retryCall.Recipients);
        Assert.Empty(await repository.GetPendingNotificationsAsync(TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task RetryPendingNotificationsAsync_does_nothing_when_everything_was_already_notified()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier();
        var useCase = CreateUseCase(
            repository,
            new ClientKeywordConfig(
                "Ecopetrol",
                new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
                new Dictionary<string, IReadOnlyList<string>> { ["tv"] = new[] { "573000000000" } }),
            notifier: notifier);
        await useCase.ExecuteAsync("CaracolTV", "tv", "file.opus", "se derramó petroleo en el pozo", BaseTime, BaseTime.AddSeconds(20));
        notifier.Calls.Clear();

        await useCase.RetryPendingNotificationsAsync();

        Assert.Empty(notifier.Calls);
    }

    private static readonly DateTimeOffset PublishedAt = new(2026, 6, 3, 9, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public async Task ExecuteWebArticleAsync_alerts_a_client_whose_keyword_matches_the_title_or_paragraph()
    {
        var repository = new InMemoryAlertRepository();
        var notifier = new FakeAlertNotifier();
        var client = new ClientKeywordConfig(
            "Ecopetrol",
            new[] { new KeywordConfig("petroleo", Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>> { ["internet"] = new[] { "573000000000" } });
        var useCase = CreateUseCase(repository, client, notifier: notifier);

        var outcome = await useCase.ExecuteWebArticleAsync(
            "El Tiempo", "https://eltiempo.com/nota", "Sube el petroleo", "El barril subió esta semana.", PublishedAt);

        Assert.Equal(new[] { "Ecopetrol" }, outcome.MatchedClients);
        Assert.Equal(new[] { "petroleo" }, outcome.MatchedKeywords);
        Assert.Equal(1, outcome.RecipientCount);
        Assert.Equal(1, outcome.NotifiedCount);
        var alert = Assert.Single(repository.Inserted);
        Assert.Equal("internet", alert.Media);
        Assert.Equal("El Tiempo", alert.Platform);
        Assert.Equal("Sube el petroleo", alert.Title);
        Assert.Equal("https://eltiempo.com/nota", alert.Url);
        Assert.Equal(AlertType.New, alert.Type);
    }

    [Fact]
    public async Task ExecuteWebArticleAsync_matches_a_keyword_that_only_appears_in_the_full_body_not_the_lead_paragraph()
    {
        // Regression: a keyword several paragraphs into the story (e.g. "Hocol, empresa del Grupo
        // Ecopetrol...") was invisible to matching when only the short lead paragraph was scanned.
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "ecopetrol"));

        var outcome = await useCase.ExecuteWebArticleAsync(
            "LaRepublica.co", "https://larepublica.co/nota", "Hocol y Colgas estrenan una red de gas",
            "La primera fase contempla una inversión cercana a $6.300 millones.", PublishedAt,
            matchText: "La primera fase contempla una inversión cercana a $6.300 millones. Colgas, Hocol, empresa del Grupo Ecopetrol, y la Alcaldía pusieron en operación la red.");

        Assert.Equal(new[] { "Ecopetrol" }, outcome.MatchedClients);
    }

    [Fact]
    public async Task ExecuteWebArticleAsync_matches_no_clients_when_no_keyword_appears()
    {
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));

        var outcome = await useCase.ExecuteWebArticleAsync(
            "El Tiempo", "https://eltiempo.com/otra", "Clima de hoy", "Lloverá en la tarde.", PublishedAt);

        Assert.Empty(outcome.MatchedClients);
        Assert.Empty(repository.Inserted);
    }

    [Fact]
    public async Task ExecuteWebArticleAsync_does_not_alert_the_same_client_for_the_same_url_twice()
    {
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));
        await useCase.ExecuteWebArticleAsync("El Tiempo", "https://eltiempo.com/nota", "Sube el petroleo", "texto", PublishedAt);

        var outcome = await useCase.ExecuteWebArticleAsync("El Tiempo", "https://eltiempo.com/nota", "Sube el petroleo", "texto", PublishedAt);

        Assert.Empty(outcome.MatchedClients);
        Assert.Single(repository.Inserted);
    }

    [Fact]
    public async Task ExecuteWebArticleAsync_does_not_apply_the_repeated_within_minute_rule()
    {
        // Two unrelated web articles from the same outlet moments apart must not collapse into
        // RepeatedWithinMinute — that rule exists for one continuous audio stream, not web outlets.
        var repository = new InMemoryAlertRepository();
        var useCase = CreateUseCase(repository, ClientConfig("Ecopetrol", "petroleo"));
        await useCase.ExecuteWebArticleAsync("El Tiempo", "https://eltiempo.com/a", "Petroleo sube", "texto a", PublishedAt);

        await useCase.ExecuteWebArticleAsync("El Tiempo", "https://eltiempo.com/b", "Mas petroleo", "texto b", PublishedAt.AddSeconds(10));

        Assert.All(repository.Inserted, alert => Assert.Equal(AlertType.New, alert.Type));
    }

    private static DetectAlertsUseCase CreateUseCase(
        InMemoryAlertRepository repository,
        ClientKeywordConfig client,
        params string[] platforms) => CreateUseCase(repository, client, notifier: null, platforms);

    private static DetectAlertsUseCase CreateUseCase(
        InMemoryAlertRepository repository,
        ClientKeywordConfig client,
        FakeAlertNotifier? notifier,
        params string[] platforms)
    {
        var configRepository = new InMemoryClientConfigRepository(
            new[] { client },
            platforms.Length > 0 ? platforms : new[] { "CaracolTV" });
        return new DetectAlertsUseCase(configRepository, repository, notifier ?? new FakeAlertNotifier());
    }

    private static ClientKeywordConfig ClientConfig(string name, string keyword)
    {
        return new ClientKeywordConfig(
            name,
            new[] { new KeywordConfig(keyword, Array.Empty<KeywordAdContext>()) },
            new Dictionary<string, IReadOnlyList<string>>());
    }

    private sealed class InMemoryClientConfigRepository : IClientConfigRepository
    {
        private readonly IReadOnlyList<ClientKeywordConfig> clients;
        private readonly IReadOnlyList<string> platforms;

        public InMemoryClientConfigRepository(IReadOnlyList<ClientKeywordConfig> clients, IReadOnlyList<string> platforms)
        {
            this.clients = clients;
            this.platforms = platforms;
        }

        public Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(clients);

        public Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(platforms);
    }

    private sealed class FakeAlertNotifier : IAlertNotifier
    {
        public List<(Alert Alert, IReadOnlyList<string> Recipients)> Calls { get; } = new();

        // By default every recipient "succeeds" — set to change which numbers NotifyAsync
        // reports back as actually delivered, for tests exercising partial-failure retry.
        public Func<string, bool> RecipientSucceeds { get; set; } = _ => true;

        public Task<IReadOnlyList<string>> NotifyAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            Calls.Add((alert, recipients));
            IReadOnlyList<string> succeeded = recipients.Where(RecipientSucceeds).ToArray();
            return Task.FromResult(succeeded);
        }
    }

    private sealed class InMemoryAlertRepository : IAlertRepository
    {
        private static readonly AlertType[] RepeatCheckTypes = { AlertType.New, AlertType.RepeatedOtherPlatform };
        private int nextId = 1;

        public List<Alert> Inserted { get; } = new();

        public Dictionary<string, IReadOnlyList<string>> Recipients { get; } = new();

        public Dictionary<string, HashSet<string>> Notified { get; } = new();

        public void Seed(Alert alert) => Inserted.Add(alert);

        public Task<string> InsertAsync(Alert alert, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            var id = (nextId++).ToString();
            Inserted.Add(alert);
            Recipients[id] = recipients;
            Notified[id] = new HashSet<string>();
            AlertsById[id] = alert;
            return Task.FromResult(id);
        }

        public Task<Alert?> GetLastNewOrRepeatedAlertAsync(string platform, string clientName, CancellationToken cancellationToken = default)
        {
            var match = Inserted
                .Where(alert => alert.Platform == platform && alert.ClientName == clientName && RepeatCheckTypes.Contains(alert.Type))
                .OrderByDescending(alert => alert.EndTime)
                .FirstOrDefault();
            return Task.FromResult(match);
        }

        public Task MarkRecipientsNotifiedAsync(string alertId, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            foreach (var number in recipients)
            {
                Notified[alertId].Add(number);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingAlertNotification>> GetPendingNotificationsAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PendingAlertNotification> pending = Recipients
                .Select(entry => new PendingAlertNotification(
                    entry.Key,
                    AlertsById[entry.Key],
                    entry.Value.Where(number => !Notified[entry.Key].Contains(number)).ToArray()))
                .Where(item => item.PendingRecipients.Count > 0)
                .ToArray();
            return Task.FromResult(pending);
        }

        public Task<bool> ExistsForUrlAsync(string url, string clientName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inserted.Any(alert =>
                alert.Url == url && alert.ClientName == clientName));

        private Dictionary<string, Alert> AlertsById { get; } = new();
    }
}
