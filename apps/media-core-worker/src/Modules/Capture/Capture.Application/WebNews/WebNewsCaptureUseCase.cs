using MediaOpsCore.Modules.Alerting.Application;

namespace MediaOpsCore.Modules.Capture.Application;

public interface IWebNewsCaptureUseCase
{
    Task ExecuteCycleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Groups keywords into search-engine-sized queries. Kept separate from any one provider's query
/// syntax (quoting, OR, the `when:1d` scope) is Google's; a future provider could need a different
/// one, but how many keywords fit in a single request is a provider-agnostic sizing decision.
/// </summary>
public static class KeywordQueryBuilder
{
    public static IReadOnlyList<string> Build(IReadOnlyList<string> keywords, int keywordsPerQuery)
    {
        if (keywords.Count == 0)
        {
            return [];
        }

        var size = Math.Max(1, keywordsPerQuery);
        var queries = new List<string>();

        for (var index = 0; index < keywords.Count; index += size)
        {
            var group = keywords.Skip(index).Take(size).Select(keyword => $"\"{keyword.Trim()}\"");
            queries.Add(string.Join(" OR ", group) + " when:1d");
        }

        return queries;
    }
}

/// <summary>
/// "Today" always means the calendar day in Bogotá, regardless of the server's own time zone or
/// the article's — a story published at 23:50 UTC is still "today" to a reader in Colombia.
/// </summary>
public static class WebNewsClock
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    public static bool IsToday(DateTimeOffset publishedAt, DateTimeOffset now) =>
        DateOnly.FromDateTime(publishedAt.ToOffset(BogotaOffset).DateTime) ==
        DateOnly.FromDateTime(now.ToOffset(BogotaOffset).DateTime);
}

/// <summary>
/// First discrete-ingestion plugin: finds today's news articles that mention a client's keywords,
/// alerts over WhatsApp, and records every article (alerted or not) for history. Google only helps
/// FIND the article — its published date, title and first paragraph always come from the outlet's
/// own page (IArticleReader), never from the search result, which is the one rule every step below
/// is built around.
///
/// Every outcome — a cycle-level failure, an article rejected, one that could not be fetched, one
/// alerted, one only partly delivered — is recorded through ISourceIncidentRecorder under the
/// synthetic source id "google-news", which is what lets the capture-status page explain an empty
/// hour on this row instead of just showing a blank.
/// </summary>
public sealed class WebNewsCaptureUseCase : IWebNewsCaptureUseCase
{
    // Source id this use case reports under — synthetic: it has no entry in the capture-source
    // catalog, it only exists as a row the status page builds purely from incidents.
    public const string SourceId = "google-news";

    private static readonly TimeSpan SeenLookback = TimeSpan.FromHours(24);

    private readonly IClientConfigRepository clientConfigRepository;
    private readonly IDetectAlertsUseCase detectAlertsUseCase;
    private readonly INewsSearchProvider newsSearchProvider;
    private readonly INewsLinkResolver newsLinkResolver;
    private readonly IArticleReader articleReader;
    private readonly IWebArticleRepository webArticleRepository;
    private readonly ISourceIncidentRecorder incidentRecorder;
    private readonly int keywordsPerQuery;
    private readonly int maxConcurrency;
    private readonly Func<DateTimeOffset> clock;

    public WebNewsCaptureUseCase(
        IClientConfigRepository clientConfigRepository,
        IDetectAlertsUseCase detectAlertsUseCase,
        INewsSearchProvider newsSearchProvider,
        INewsLinkResolver newsLinkResolver,
        IArticleReader articleReader,
        IWebArticleRepository webArticleRepository,
        ISourceIncidentRecorder incidentRecorder,
        int keywordsPerQuery,
        int maxConcurrency,
        Func<DateTimeOffset>? clock = null)
    {
        this.clientConfigRepository = clientConfigRepository;
        this.detectAlertsUseCase = detectAlertsUseCase;
        this.newsSearchProvider = newsSearchProvider;
        this.newsLinkResolver = newsLinkResolver;
        this.articleReader = articleReader;
        this.webArticleRepository = webArticleRepository;
        this.incidentRecorder = incidentRecorder;
        this.keywordsPerQuery = keywordsPerQuery;
        this.maxConcurrency = maxConcurrency;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
    {
        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);
        var keywords = clients
            .SelectMany(client => client.Keywords.Select(keyword => keyword.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keywords.Length == 0)
        {
            return;
        }

        var queries = KeywordQueryBuilder.Build(keywords, keywordsPerQuery);
        var found = new Dictionary<string, NewsSearchItem>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            IReadOnlyList<NewsSearchItem> items;
            try
            {
                items = await newsSearchProvider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One query failing (most often Google rate-limiting) means the rest are likely to
                // fail the same way right now — stop the cycle rather than hammering it further;
                // nothing processed so far was marked seen, so the next cycle retries everything.
                incidentRecorder.Record(SourceId, IncidentKinds.CycleFailed, $"No se pudo consultar Google Noticias: {exception.Message}");
                return;
            }

            foreach (var item in items)
            {
                found[item.GoogleId] = item;
            }
        }

        var seenIds = await webArticleRepository.GetSeenGoogleIdsAsync(SeenLookback, cancellationToken).ConfigureAwait(false);
        var newItems = found.Values.Where(item => !seenIds.Contains(item.GoogleId)).ToArray();

        var alerted = 0;
        var rejected = 0;
        var failed = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        await Task.WhenAll(newItems.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                switch (await ProcessArticleAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    case ArticleOutcome.Alerted:
                        Interlocked.Increment(ref alerted);
                        break;
                    case ArticleOutcome.Rejected:
                        Interlocked.Increment(ref rejected);
                        break;
                    case ArticleOutcome.Failed:
                        Interlocked.Increment(ref failed);
                        break;
                }
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        incidentRecorder.Record(
            SourceId,
            IncidentKinds.CycleOk,
            $"{queries.Count} consulta(s) · {found.Count} nota(s) · {newItems.Length} nueva(s) · {alerted} alertada(s) · {rejected} rechazada(s) · {failed} con fallo");
    }

    private enum ArticleOutcome
    {
        Alerted,
        Rejected,
        Failed,
    }

    private async Task<ArticleOutcome> ProcessArticleAsync(NewsSearchItem item, CancellationToken cancellationToken)
    {
        try
        {
            var linkResolution = await newsLinkResolver.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            if (!linkResolution.Succeeded)
            {
                await SaveAsync(item, item.GoogleLink, WebArticleStatus.Failed, item.Title, null, [], [], cancellationToken).ConfigureAwait(false);
                incidentRecorder.Record(SourceId, IncidentKinds.ArticleFailed, $"{item.Outlet} — no se pudo resolver el enlace: {linkResolution.ErrorMessage}");
                return ArticleOutcome.Failed;
            }

            var url = linkResolution.Url!;
            var content = await articleReader.ReadAsync(url, cancellationToken).ConfigureAwait(false);
            if (!content.Succeeded)
            {
                await SaveAsync(item, url, WebArticleStatus.Failed, content.Title ?? item.Title, null, [], [], cancellationToken).ConfigureAwait(false);
                incidentRecorder.Record(SourceId, IncidentKinds.ArticleFailed, $"{item.Outlet} — {item.Title} — no se pudo extraer el contenido: {content.ErrorMessage}");
                return ArticleOutcome.Failed;
            }

            var title = content.Title ?? item.Title;

            if (content.PublishedAt is null)
            {
                await SaveAsync(item, url, WebArticleStatus.NoDate, title, content.FirstParagraph, [], [], cancellationToken).ConfigureAwait(false);
                incidentRecorder.Record(SourceId, IncidentKinds.ArticleRejected, $"{item.Outlet} — {title} — sin fecha en la página");
                return ArticleOutcome.Rejected;
            }

            if (!WebNewsClock.IsToday(content.PublishedAt.Value, clock()))
            {
                await SaveAsync(item, url, WebArticleStatus.NotToday, title, content.FirstParagraph, [], [], cancellationToken).ConfigureAwait(false);
                incidentRecorder.Record(
                    SourceId, IncidentKinds.ArticleRejected,
                    $"{item.Outlet} — {title} — no es de hoy ({content.PublishedAt:dd-MMM HH:mm})");
                return ArticleOutcome.Rejected;
            }

            var outcome = await detectAlertsUseCase
                .ExecuteWebArticleAsync(item.Outlet, url, title, content.FirstParagraph ?? string.Empty, content.PublishedAt.Value, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.MatchedClients.Count == 0)
            {
                await SaveAsync(item, url, WebArticleStatus.NoKeyword, title, content.FirstParagraph, [], [], cancellationToken).ConfigureAwait(false);
                incidentRecorder.Record(SourceId, IncidentKinds.ArticleRejected, $"{item.Outlet} — {title} — sin palabra clave");
                return ArticleOutcome.Rejected;
            }

            await SaveAsync(item, url, WebArticleStatus.Alerted, title, content.FirstParagraph, outcome.MatchedKeywords, outcome.MatchedClients, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.NotifiedCount < outcome.RecipientCount)
            {
                incidentRecorder.Record(
                    SourceId, IncidentKinds.SendFailed,
                    $"{item.Outlet} — {title} — enviada a {outcome.NotifiedCount}/{outcome.RecipientCount}; se reintentará");
            }
            else
            {
                incidentRecorder.Record(
                    SourceId, IncidentKinds.ArticleAlerted,
                    $"{item.Outlet} — {title} — enviada a {outcome.NotifiedCount}/{outcome.RecipientCount}");
            }

            return ArticleOutcome.Alerted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One bad article must never take the whole cycle down with it.
            incidentRecorder.Record(SourceId, IncidentKinds.ArticleFailed, $"{item.Outlet} — {item.Title} — error inesperado: {exception.Message}");
            return ArticleOutcome.Failed;
        }
    }

    private Task SaveAsync(
        NewsSearchItem item,
        string url,
        string status,
        string? title,
        string? firstParagraph,
        IReadOnlyList<string> matchedKeywords,
        IReadOnlyList<string> clients,
        CancellationToken cancellationToken) =>
        webArticleRepository.SaveAsync(
            new WebArticleRecord(item.GoogleId, url, item.Outlet, title, firstParagraph, null, status, matchedKeywords, clients, clock()),
            cancellationToken);
}
