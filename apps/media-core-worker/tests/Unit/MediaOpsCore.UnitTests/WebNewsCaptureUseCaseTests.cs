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
    public void IsToday_compares_the_published_date_as_printed_without_converting_time_zones()
    {
        // 06-oct 00:26 UTC is 05-oct 19:26 in Bogota, but the page says the 6th: that is what counts.
        var publishedAt = new DateTimeOffset(2026, 10, 6, 0, 26, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, Bogota);

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
    public async Task ExecuteCycleAsync_passes_the_articles_full_text_to_keyword_matching_not_just_the_lead_paragraph()
    {
        // Regression: a client's keyword several paragraphs into the body (not the short lead
        // paragraph an alert displays) must still be checked.
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["Ecopetrol"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://larepublica.co/nota" },
            reader: new FakeArticleReader
            {
                Content = new ArticleContent(
                    "Hocol y Colgas estrenan una red de gas", "La primera fase contempla una inversión.", PublishedToday, null,
                    FullText: "La primera fase contempla una inversión. Colgas, Hocol, empresa del Grupo Ecopetrol, pusieron en operación la red."),
            },
            detectAlertsUseCase: alertUseCase);

        await sut.ExecuteCycleAsync();

        Assert.Contains("Ecopetrol", alertUseCase.LastMatchText);
    }

    [Fact]
    public async Task ExecuteCycleAsync_saves_a_blocked_article_as_final_so_it_is_not_retried()
    {
        // Cloudflare blocked even the browser fallback: retrying would hit the same wall, so the
        // note is stored as blocked (not failed, which is what the retry picks up) and says so.
        var recorder = new FakeIncidentRecorder();
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://cambiocolombia.com/nota" },
            reader: new FakeArticleReader { Content = new ArticleContent(null, null, null, "Bloqueado por Cloudflare (anti-bot)", Blocked: true) },
            repository: repository,
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        Assert.Equal(WebArticleStatus.Blocked, Assert.Single(repository.Saved).Status);
        var incident = recorder.Records.Single(r => r.Kind == IncidentKinds.ArticleFailed);
        Assert.Contains("no se reintentará", incident.Reason);
        Assert.Contains("https://cambiocolombia.com/nota", incident.Reason);
    }

    [Fact]
    public async Task ExecuteCycleAsync_includes_the_article_url_in_a_rejected_notes_reason()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/nota-x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Clima", "Hoy lloverá en la tarde", PublishedToday, null) },
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        var rejected = recorder.Records.Single(r => r.Kind == IncidentKinds.ArticleRejected);
        Assert.Contains("https://eltiempo.com/nota-x", rejected.Reason);
    }

    [Fact]
    public async Task ExecuteCycleAsync_includes_googles_own_link_when_the_real_url_could_not_be_resolved()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "petroleo")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { ErrorMessage = "Google no entregó la firma" },
            recorder: recorder);

        await sut.ExecuteCycleAsync();

        var failed = recorder.Records.Single(r => r.Kind == IncidentKinds.ArticleFailed);
        Assert.Contains("https://news.google.com/rss/articles/a1", failed.Reason);
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

    [Fact]
    public async Task ExecuteCycleAsync_saves_a_review_alert_only_once_a_failing_article_runs_out_of_retries()
    {
        // The 403 of La Silla Vacía: a transient block may clear on a retry (and then it is a normal alert),
        // so the title-and-link alert waits for the last attempt.
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "La Silla responde a Ecopetrol", "La Silla Vacía")] },
            resolver: new FakeNewsLinkResolver { Url = "https://www.lasillavacia.com/nota" },
            reader: new FakeArticleReader { Content = new ArticleContent(null, null, null, "El sitio respondió HTTP 403") },
            detectAlertsUseCase: alerts);

        await sut.ExecuteCycleAsync();
        await sut.ExecuteCycleAsync();
        Assert.Empty(alerts.Unreadable);

        await sut.ExecuteCycleAsync();

        var saved = Assert.Single(alerts.Unreadable);
        Assert.Equal("La Silla Vacía", saved.Outlet);
        Assert.Equal("https://www.lasillavacia.com/nota", saved.Url);
        Assert.Equal("La Silla responde a Ecopetrol", saved.Title);
        Assert.Contains("HTTP 403", saved.Reason);
    }

    [Fact]
    public async Task ExecuteCycleAsync_saves_a_review_alert_at_once_for_a_blocked_article()
    {
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "Ecopetrol anuncia")] },
            resolver: new FakeNewsLinkResolver { Url = "https://cambiocolombia.com/nota" },
            reader: new FakeArticleReader { Content = new ArticleContent(null, null, null, "Bloqueado por Cloudflare (anti-bot)", Blocked: true) },
            detectAlertsUseCase: alerts);

        await sut.ExecuteCycleAsync();

        var saved = Assert.Single(alerts.Unreadable);
        Assert.Equal("https://cambiocolombia.com/nota", saved.Url);
        Assert.Contains("Cloudflare", saved.Reason);
    }

    [Fact]
    public async Task ExecuteCycleAsync_saves_a_review_alert_at_once_for_a_page_without_a_date_keeping_what_was_read()
    {
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "Nuevo gasoducto")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader
            {
                Content = new ArticleContent("Nuevo gasoducto", "Se firmó el contrato.", null, null, FullText: "Se firmó el contrato con Ecopetrol."),
            },
            detectAlertsUseCase: alerts);

        await sut.ExecuteCycleAsync();

        var saved = Assert.Single(alerts.Unreadable);
        Assert.Contains("fecha", saved.Reason);
        Assert.Equal("Se firmó el contrato.", saved.FirstParagraph);
        Assert.Equal("Se firmó el contrato con Ecopetrol.", saved.MatchText);
    }

    [Fact]
    public async Task ExecuteCycleAsync_saves_no_review_alert_for_an_article_confirmed_not_to_be_from_today()
    {
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "Ecopetrol anuncia")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Ecopetrol anuncia", "Parrafo", PublishedYesterday, null) },
            detectAlertsUseCase: alerts);

        await sut.ExecuteCycleAsync();

        Assert.Empty(alerts.Unreadable);
    }

    [Fact]
    public async Task ExecuteCycleAsync_saves_a_review_alert_with_googles_link_when_the_real_one_never_resolves()
    {
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "Ecopetrol anuncia")] },
            resolver: new FakeNewsLinkResolver { ErrorMessage = "Google no devolvió la nota" },
            detectAlertsUseCase: alerts);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await sut.ExecuteCycleAsync();
        }

        var saved = Assert.Single(alerts.Unreadable);
        Assert.Equal("https://news.google.com/rss/articles/a1", saved.Url);
        Assert.Contains("enlace", saved.Reason);
    }

    [Fact]
    public async Task ExecuteCycleAsync_counts_an_unexpected_error_as_a_failed_attempt_instead_of_retrying_forever()
    {
        // Before: an exception only left an incident, nothing was stored, so the article came back every cycle.
        var repository = new FakeWebArticleRepository();
        var alerts = new FakeDetectAlertsUseCase();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "Ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1", "Ecopetrol anuncia")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/boom" },
            reader: new FakeArticleReader { ThrowOn = "boom" },
            repository: repository,
            detectAlertsUseCase: alerts);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await sut.ExecuteCycleAsync();
        }

        Assert.Equal(3, repository.Saved.Count(record => record.Status == WebArticleStatus.Failed));
        Assert.Single(alerts.Unreadable);
    }

    private static NewsSearchItem DirectItem(string url, string outlet = "La Silla Vacía") =>
        new($"url:{url}", "Titulo", outlet, url, Now, DirectLink: true);

    [Fact]
    public async Task ExecuteCycleAsync_asks_the_direct_search_for_the_first_keyword_of_each_client_only()
    {
        var direct = new FakeNewsSearchProvider();
        var sut = Build(
            clients:
            [
                new ClientKeywordConfig("Ecopetrol", [new KeywordConfig("ecopetrol", []), new KeywordConfig("reficar", []), new KeywordConfig("cenit", [])],
                    new Dictionary<string, IReadOnlyList<string>>()),
                ClientWithKeyword("Otro", "Nada"),
            ],
            directSearch: direct);

        await sut.ExecuteCycleAsync();

        Assert.Equal(["\"ecopetrol\"", "\"Nada\""], direct.Queries);
    }

    [Fact]
    public async Task ExecuteCycleAsync_alerts_a_note_that_only_the_direct_search_found_without_resolving_its_link()
    {
        // The keyword is in the body of the page, not in the headline: only a search of the text offers it.
        var resolver = new FakeNewsLinkResolver();
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            resolver: resolver,
            reader: new FakeArticleReader { Content = new ArticleContent("La rosca de De la Espriella", "Ecopetrol transfirió", PublishedToday, null) },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            directSearch: new FakeNewsSearchProvider { Items = [DirectItem("https://www.lasillavacia.com/silla-nacional/la-rosca/")] });

        await sut.ExecuteCycleAsync();

        var saved = Assert.Single(repository.Saved);
        Assert.Equal(WebArticleStatus.Alerted, saved.Status);
        Assert.Equal("https://www.lasillavacia.com/silla-nacional/la-rosca/", saved.Url);
        Assert.False(resolver.WasCalled); // it is already the real address
    }

    [Fact]
    public async Task ExecuteCycleAsync_processes_once_a_note_that_both_searches_found()
    {
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Titulo", "Texto", PublishedToday, null) },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            directSearch: new FakeNewsSearchProvider { Items = [DirectItem("https://eltiempo.com/x")] });

        await sut.ExecuteCycleAsync();

        // One by RSS (resolved to the same address) and one by the direct search: the alert use case sees the address once.
        Assert.Equal(1, alertUseCase.Calls.Count(call => call == "https://eltiempo.com/x"));
        Assert.Equal(1, repository.Saved.Count(record => record.Url == "https://eltiempo.com/x" && record.Status == WebArticleStatus.Alerted));
    }

    [Fact]
    public async Task ExecuteCycleAsync_skips_a_direct_note_already_seen()
    {
        var repository = new FakeWebArticleRepository();
        repository.SeenIds.Add("url:https://www.lasillavacia.com/x");
        var reader = new FakeArticleReader { Content = new ArticleContent("T", "P", PublishedToday, null) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")], reader: reader, repository: repository,
            directSearch: new FakeNewsSearchProvider { Items = [DirectItem("https://www.lasillavacia.com/x")] });

        await sut.ExecuteCycleAsync();

        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task ExecuteCycleAsync_keeps_going_when_the_direct_search_fails()
    {
        var recorder = new FakeIncidentRecorder();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("T", "P", PublishedToday, null) },
            recorder: recorder,
            directSearch: new FakeNewsSearchProvider { Exception = new HttpRequestException("api down") });

        await sut.ExecuteCycleAsync();

        // The RSS part ran and the cycle counts as good: the API is an extra, not the way in.
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.CycleOk);
        Assert.DoesNotContain(recorder.Records, r => r.Kind == IncidentKinds.CycleFailed);
    }

    private sealed class FakeFeedSource : INewsFeedSource
    {
        public IReadOnlyList<NewsSearchItem> Items { get; init; } = [];
        public Exception? Exception { get; init; }

        public Task<IReadOnlyList<NewsSearchItem>> ReadAsync(CancellationToken cancellationToken = default) =>
            Exception is not null ? throw Exception : Task.FromResult(Items);
    }

    [Fact]
    public async Task ExecuteCycleAsync_alerts_a_note_that_only_the_google_alerts_feed_brought()
    {
        // Google Alerts searches the whole text: a story naming the client only in its body.
        var resolver = new FakeNewsLinkResolver();
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) };
        var recorder = new FakeIncidentRecorder();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            resolver: resolver,
            reader: new FakeArticleReader { Content = new ArticleContent("Cronograma del gasoducto", "Ecopetrol rehabilitará", PublishedToday, null) },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            recorder: recorder,
            feed: new FakeFeedSource { Items = [DirectItem("https://www.portafolio.co/energia/gasoducto-505716", "portafolio.co")] });

        await sut.ExecuteCycleAsync();

        var saved = Assert.Single(repository.Saved);
        Assert.Equal(WebArticleStatus.Alerted, saved.Status);
        Assert.Equal("https://www.portafolio.co/energia/gasoducto-505716", saved.Url);
        Assert.False(resolver.WasCalled);
        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.CycleOk && r.Reason!.Contains("1 de Google Alerts"));
    }

    [Fact]
    public async Task ExecuteCycleAsync_names_a_feed_note_after_the_outlet_its_page_declares_not_its_domain()
    {
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            reader: new FakeArticleReader { Content = new ArticleContent("Titulo", "Ecopetrol", PublishedToday, null, SiteName: "PETROGUIA") },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            feed: new FakeFeedSource { Items = [DirectItem("https://www.petroguia.com/pet/noticias/a", "petroguia.com")] });

        await sut.ExecuteCycleAsync();

        Assert.Equal("PETROGUIA", Assert.Single(repository.Saved).Outlet);
        Assert.Equal("PETROGUIA", alertUseCase.Outlets.Single());
    }

    [Fact]
    public async Task ExecuteCycleAsync_keeps_the_outlet_name_the_search_gave()
    {
        // Google News already names the outlet ("El Tiempo"): the page's own name does not replace it.
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("T", "P", PublishedToday, null, SiteName: "EL TIEMPO - Noticias") },
            repository: repository,
            detectAlertsUseCase: new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) });

        await sut.ExecuteCycleAsync();

        Assert.Equal(Item("a1").Outlet, Assert.Single(repository.Saved).Outlet);
    }

    [Fact]
    public async Task ExecuteCycleAsync_processes_once_a_note_that_the_rss_and_the_feed_both_brought()
    {
        var repository = new FakeWebArticleRepository();
        var alertUseCase = new FakeDetectAlertsUseCase { Outcome = new WebArticleAlertOutcome(["Ecopetrol"], ["ecopetrol"], 1, 1) };
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("Titulo", "Texto", PublishedToday, null) },
            repository: repository,
            detectAlertsUseCase: alertUseCase,
            feed: new FakeFeedSource { Items = [DirectItem("https://eltiempo.com/x", "eltiempo.com")] });

        await sut.ExecuteCycleAsync();

        Assert.Equal(1, alertUseCase.Calls.Count(call => call == "https://eltiempo.com/x"));
    }

    [Fact]
    public async Task ExecuteCycleAsync_keeps_going_when_the_google_alerts_feed_fails()
    {
        var recorder = new FakeIncidentRecorder();
        var repository = new FakeWebArticleRepository();
        var sut = Build(
            clients: [ClientWithKeyword("Ecopetrol", "ecopetrol")],
            search: new FakeNewsSearchProvider { Items = [Item("a1")] },
            resolver: new FakeNewsLinkResolver { Url = "https://eltiempo.com/x" },
            reader: new FakeArticleReader { Content = new ArticleContent("T", "P", PublishedToday, null) },
            repository: repository,
            recorder: recorder,
            feed: new FakeFeedSource { Exception = new HttpRequestException("feed down") });

        await sut.ExecuteCycleAsync();

        Assert.Contains(recorder.Records, r => r.Kind == IncidentKinds.CycleOk);
        Assert.DoesNotContain(recorder.Records, r => r.Kind == IncidentKinds.CycleFailed);
        Assert.Single(repository.Saved); // the RSS note was still processed
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
        FakeIncidentRecorder? recorder = null,
        INewsSearchProvider? directSearch = null,
        INewsFeedSource? feed = null) =>
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
            clock: () => Now,
            directSearchProvider: directSearch,
            feedSource: feed);

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
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<NewsSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Exception is not null ? throw Exception : Task.FromResult(Items);
        }
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
        // The real repository counts failed attempts per article and gives up at this many.
        public int MaxFailedAttempts { get; set; } = 3;
        private readonly Dictionary<string, int> failedAttempts = new(StringComparer.Ordinal);

        public Task<IReadOnlySet<string>> GetSeenGoogleIdsAsync(TimeSpan lookback, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(SeenIds);

        public Task<bool> SaveAsync(WebArticleRecord record, CancellationToken cancellationToken = default)
        {
            Saved.Add(record);
            if (record.Status != WebArticleStatus.Failed)
            {
                return Task.FromResult(true);
            }

            failedAttempts[record.GoogleId] = failedAttempts.GetValueOrDefault(record.GoogleId) + 1;
            return Task.FromResult(failedAttempts[record.GoogleId] >= MaxFailedAttempts);
        }
    }

    private sealed class FakeDetectAlertsUseCase : IDetectAlertsUseCase
    {
        public WebArticleAlertOutcome Outcome { get; set; } = new([], [], 0, 0);
        public string? LastMatchText { get; private set; }
        public List<string> Calls { get; } = [];
        public List<string> Outlets { get; } = [];
        public List<(string Outlet, string Url, string Title, string Reason, string? FirstParagraph, string? MatchText)> Unreadable { get; } = [];

        public Task ExecuteAsync(string platform, string media, string filePath, string text, DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RetryPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WebArticleAlertOutcome> ExecuteWebArticleAsync(string outlet, string url, string title, string firstParagraph, DateTimeOffset publishedAt, string? matchText = null, CancellationToken cancellationToken = default)
        {
            LastMatchText = matchText;
            Calls.Add(url);
            Outlets.Add(outlet);
            return Task.FromResult(Outcome);
        }

        public Task<int> ExecuteUnreadableWebArticleAsync(string outlet, string url, string title, string reason, string? firstParagraph = null, string? matchText = null, CancellationToken cancellationToken = default)
        {
            Unreadable.Add((outlet, url, title, reason, firstParagraph, matchText));
            return Task.FromResult(1);
        }
    }

    private sealed class FakeIncidentRecorder : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind, string? Reason)> Records { get; } = [];

        public void Record(string sourceId, string kind, string? reason = null) => Records.Add((sourceId, kind, reason));
    }
}
