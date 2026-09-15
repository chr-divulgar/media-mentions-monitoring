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
            Array.Empty<string>(),
            Array.Empty<string>());
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
    public async Task ExecuteAsync_classifies_as_repeated_other_platform_when_context_matches_another_platforms_last_alert()
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
        Assert.Equal(AlertType.RepeatedOtherPlatform, alert.Type);
    }

    private static DetectAlertsUseCase CreateUseCase(
        InMemoryAlertRepository repository,
        ClientKeywordConfig client,
        params string[] platforms)
    {
        var configRepository = new InMemoryClientConfigRepository(
            new[] { client },
            platforms.Length > 0 ? platforms : new[] { "CaracolTV" });
        return new DetectAlertsUseCase(configRepository, repository);
    }

    private static ClientKeywordConfig ClientConfig(string name, string keyword)
    {
        return new ClientKeywordConfig(
            name,
            new[] { new KeywordConfig(keyword, Array.Empty<KeywordAdContext>()) },
            Array.Empty<string>(),
            Array.Empty<string>());
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

    private sealed class InMemoryAlertRepository : IAlertRepository
    {
        private static readonly AlertType[] RepeatCheckTypes = { AlertType.New, AlertType.RepeatedOtherPlatform };

        public List<Alert> Inserted { get; } = new();

        public void Seed(Alert alert) => Inserted.Add(alert);

        public Task InsertAsync(Alert alert, CancellationToken cancellationToken = default)
        {
            Inserted.Add(alert);
            return Task.CompletedTask;
        }

        public Task<Alert?> GetLastNewOrRepeatedAlertAsync(string platform, string clientName, CancellationToken cancellationToken = default)
        {
            var match = Inserted
                .Where(alert => alert.Platform == platform && alert.ClientName == clientName && RepeatCheckTypes.Contains(alert.Type))
                .OrderByDescending(alert => alert.EndTime)
                .FirstOrDefault();
            return Task.FromResult(match);
        }
    }
}
