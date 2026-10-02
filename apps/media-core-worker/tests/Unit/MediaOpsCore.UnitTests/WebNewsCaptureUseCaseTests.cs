using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Modules.Capture.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class KeywordQueryBuilderTests
{
    [Fact]
    public void Build_returns_nothing_for_an_empty_keyword_list()
    {
        Assert.Empty(KeywordQueryBuilder.Build([], 8));
    }

    [Fact]
    public void Build_groups_keywords_into_quoted_or_queries_scoped_to_the_last_day()
    {
        var queries = KeywordQueryBuilder.Build(["Ecopetrol", "petroleo"], 8);

        Assert.Equal(["\"Ecopetrol\" OR \"petroleo\" when:1d"], queries);
    }

    [Fact]
    public void Build_splits_into_several_queries_once_the_group_size_is_exceeded()
    {
        var keywords = Enumerable.Range(1, 20).Select(i => $"k{i}").ToArray();

        var queries = KeywordQueryBuilder.Build(keywords, 8);

        Assert.Equal(3, queries.Count);
        Assert.Equal(8, queries[0].Split(" OR ").Length);
        Assert.Equal(4, queries[2].Split(" OR ").Length);
    }
}

public sealed class WebNewsClockTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    [Fact]
    public void IsToday_is_true_for_the_same_bogota_calendar_day_in_a_different_offset()
    {
        var publishedAt = new DateTimeOffset(2026, 10, 2, 23, 50, 0, TimeSpan.Zero); // 18:50 Bogotá
        var now = new DateTimeOffset(2026, 10, 2, 20, 0, 0, Bogota);

        Assert.True(WebNewsClock.IsToday(publishedAt, now));
    }

    [Fact]
    public void IsToday_is_false_just_after_midnight_bogota_for_yesterdays_article()
    {
        var publishedAt = new DateTimeOffset(2026, 10, 1, 23, 59, 0, Bogota);
        var now = new DateTimeOffset(2026, 10, 2, 0, 1, 0, Bogota);

        Assert.False(WebNewsClock.IsToday(publishedAt, now));
    }
}

public sealed class WebNewsCaptureUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 15, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset PublishedToday = new(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset PublishedYesterday = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-5));

    private static NewsSearchItem Item(string id, string title = "Titulo", string outlet = "El Tiempo") =>
        new(id, title, outlet, $"https://news.google.com/rss/articles/{id}", Now);

    [Fact]
    public async Task ExecuteCycleAsync_does_nothing_when_no_client_has_keywords()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = Build(clients: [], recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Empty(recorder.Records);
    }

    [Fact]
    public async Task ExecuteCycleAsync_skips_articles_already_seen_in_the_lookback_window()
    {
        var repository = new FakeWebArticleRepository { SeenIds = { "a1" } };
        var search = new FakeNewsSearchProvider { Items = [Item("a1")] };
        var resolver = new FakeNewsLinkResolver();
        var recorder = new FakeIncidentRecorder();
        var sut = Build(clients: [ClientWithKeyword("Ecopetrol", "petroleo")], search: search, resolver: resolver, repository: repository, recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.False(resolver.WasCalled);
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.CycleOk && r.Reason!.Contains("0 nueva"));
    }

    [Fact]
    public async Task ExecuteCycleAsync_rejects_an_article_with_no_publish_date()
    {
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Titulo", "Parrafo sobre petroleo", null, null) },
            repository: repository);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.NoDate, Assert.Single(repository.Saved).Status);
    }

    [Fact]
    public async Task ExecuteCycleAsync_rejects_an_article_not_published_today()
    {
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Titulo", "Parrafo sobre petroleo", PublishedYesterday, null) },
            repository: repository);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.NotToday, Assert.Single(repository.Saved).Status);
    }

    [Fact]
    public async Task ExecuteCycleAsync_rejects_a_todays_article_with_no_matching_keyword()
    {
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Clima", "Hoy lloverá en la tarde", PublishedToday, null) },
            repository: repository);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.NoKeyword, Assert.Single(repository.Saved).Status);
    }

    [Fact]
    public async Task ExecuteCycleAsync_alerts_a_todays_article_that_matches_a_keyword_and_records_it()
    {
        var recorder = new FakeIncidentRecorder();
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["petroleo"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Sube el petroleo", "El barril subió", PublishedToday, null) },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.Alerted, Assert.Single(repository.Saved).Status);
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.ArticleAlerted);
    }

    [Fact]
    public async Task ExecuteCycleAsync_records_a_partial_delivery_as_send_failed()
    {
        var recorder = new FakeIncidentRecorder();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["petroleo"], 2, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Sube el petroleo", "El barril subió", PublishedToday, null) },
            detectAlertsUseCase: alertUseCase,
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.SendFailed);
    }

    [Fact]
    public async Task ExecuteCycleAsync_records_a_failed_link_resolution_without_saving_a_resolved_url()
    {
        var recorder = new FakeIncidentRecorder();
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { ErrorMessage = "Google no entregó la firma" },
            repository: repository,
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.Failed, Assert.Single(repository.Saved).Status);
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.ArticleFailed);
    }

    [Fact]
    public async Task ExecuteCycleAsync_aborts_the_whole_cycle_without_marking_anything_seen_when_search_fails()
    {
        var recorder = new FakeIncidentRecorder();
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Exception = new HttpRequestException("429") },
            repository: repository,
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Empty(repository.Saved);
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.CycleFailed);
        Assert.DoesNotContain(recorder.Records, r => r.Kind == IncidentKinds.CycleOk);
    }

    [Fact]
    public async Task ExecuteCycleAsync_keeps_processing_other_articles_when_one_throws_unexpectedly()
    {
        var repository = new FakeWebArticleRepository();
        var reader = new FakeArticleReader { ThrowOn = "a1", Content = new ArticleContent("T", "Parrafo sobre petroleo", PublishedToday, null) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1"), Item("a2")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: reader,
            repository: repository,
            detectAlertsUseCase: new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome([], [], 0, 0) });

        await sut.ExecuteCycleAsync();

        Assert.Equal(2, repository.Saved.Count);
    }

    private static ClientKeywordConfig ClientWithKeyword(string name, string keyword) =>
        new(name, [new KeywordConfig(keyword, [])], new Dictionary<string, IReadOnlyList<string>>());

    private static WebNewsCaptureUseCase Build(
        IReadOnlyList<ClientKeywordConfig> clients,
        FakeNewsSearchProvider? search = null,
        FakeNewsLinkResolver? resolver = null,
        FakeArticleReader? reader = null,
        FakeWebArticleRepository? repository = null,
        FakeDetectAlertsUseCase? detectAlertsUseCase = null,
        FakeIncidentRecorder? recorder = null) =>
        new(
            new FakeClientConfigRepository(clients),
            detectAlertsUseCase ?? new FakeDetectAlertsUseCase(),
            search ?? new FakeNewsSearchProvider(),
            resolver ?? new FakeNewsLinkResolver(),
            reader ?? new FakeArticleReader(),
            repository ?? new FakeWebArticleRepository(),
            recorder ?? new FakeIncidentRecorder(),
            keywordsPerQuery: 8,
            maxConcurrency: 4,
            clock: () => Now);

    private sealed class FakeClientConfigRepository(IReadOnlyList<ClientKeywordConfig> clients) : IClientConfigRepository
    {
        public Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(clients);

        public Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeNewsSearchProvider : INewsSearchProvider
    {
        public IReadOnlyList<NewsSearchItem> Items { get; set; } = [];
        public Exception? Exception { get; set; }

        public Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Exception is not null ? throw Exception : Task.FromResult(Items);
    }

    private sealed class FakeNewsLinkResolver : INewsLinkResolver
    {
        public string? Url { get; set; }
        public string? ErrorMessage { get; set; }
        public bool WasCalled { get; private set; }

        public Task<NewsLinkResolution> ResolveAsync(NewsSearchItem item, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new NewsLinkResolution(Url, ErrorMessage));
        }
    }

    private sealed class FakeArticleReader : IArticleReader
    {
        public ArticleContent Content { get; set; } = new(null, null, null, "no configurado");
        public string? ThrowOn { get; set; }

        public Task<ArticleContent> ReadAsync(string url, CancellationToken cancellationToken = default)
        {
            if (ThrowOn is not null && url.Contains(ThrowOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(Content);
        }
    }

    private sealed class FakeWebArticleRepository : IWebArticleRepository
    {
        public HashSet<string> SeenIds { get; } = new(StringComparer.Ordinal);
        public List<WebArticleRecord> Saved { get; } = [];

        public Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(SeenIds);

        public Task SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default)
        {
            Saved.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDetectAlertsUseCase : IDetectAlertsUseCase
    {
        public WebArticleAlertOutcome Outcome { get; set; } = new([], [], 0, 0);

        public Task ExecuteAsync(string platform, string media, string filePath, string text, DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RetryPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WebArticleAlertOutcome> ExecuteWebArticleAsync(string outlet, string url, string title, string firstParagraph, DateTimeOffset publishedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome);
    }

    private sealed class FakeIncidentRecorder : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];

        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }
}
