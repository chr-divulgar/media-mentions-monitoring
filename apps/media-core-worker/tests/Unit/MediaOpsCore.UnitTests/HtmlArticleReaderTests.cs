using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class HtmlArticleReaderTests
{
    [Fact]
    public async Task ParseAsync_reads_title_and_date_from_json_ld_and_the_first_long_paragraph()
    {
        const string html = """
            <html><head>
              <script type="application/ld+json">
                {"@type":"NewsArticle","headline":"Sube el petroleo","datePublished":"2026-10-02T07:42:08-05:00"}
              </script>
            </head><body>
              <article>
                <figure><figcaption>Foto: Canva, no es el primer párrafo</figcaption></figure>
                <p>Corto.</p>
                <p>El barril de petroleo subió esta semana tras el anuncio de la OPEP sobre nuevos recortes de producción en la región.</p>
              </article>
            </body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal("Sube el petroleo", content.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 7, 42, 8, TimeSpan.FromHours(-5)), content.PublishedAt);
        Assert.StartsWith("El barril de petroleo subió", content.FirstParagraph);
        Assert.True(content.Succeeded);
    }

    [Fact]
    public async Task ParseAsync_falls_back_to_meta_tags_when_there_is_no_json_ld()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="Clima de hoy" />
              <meta property="article:published_time" content="2026-10-02T09:00:00-05:00" />
              <meta property="og:description" content="Lloverá en la tarde en varias regiones del país." />
            </head><body></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal("Clima de hoy", content.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
        Assert.Equal("Lloverá en la tarde en varias regiones del país.", content.FirstParagraph);
    }

    [Fact]
    public async Task ParseAsync_returns_a_null_date_when_the_page_has_none()
    {
        const string html = "<html><head><title>Sin fecha</title></head><body><p>Texto cualquiera sin fecha publicada en ningún lado de la página.</p></body></html>";

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Null(content.PublishedAt);
        Assert.Equal("Sin fecha", content.Title);
    }

    [Fact]
    public async Task ParseAsync_skips_short_paragraphs_and_picks_the_first_long_one()
    {
        const string html = """
            <html><body><article>
              <p>Foto: AFP</p>
              <p>Este es un párrafo lo suficientemente largo como para ser considerado el primer párrafo real de la noticia completa.</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.StartsWith("Este es un párrafo lo suficientemente largo", content.FirstParagraph);
    }

    [Fact]
    public async Task ParseAsync_keeps_only_the_first_sentence_of_a_multi_sentence_paragraph()
    {
        const string html = """
            <html><body><article>
              <p>El barril de petroleo subió esta semana. Analistas lo atribuyen al anuncio de la OPEP sobre nuevos recortes de producción en la región y advierten más volatilidad.</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal("El barril de petroleo subió esta semana.", content.FirstParagraph);
    }

    [Fact]
    public async Task ParseAsync_keeps_the_whole_paragraph_when_it_has_no_sentence_ending_punctuation()
    {
        const string html = """
            <html><body><article>
              <p>El barril de petroleo subió esta semana sin ningún punto final en todo este texto tan largo</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal("El barril de petroleo subió esta semana sin ningún punto final en todo este texto tan largo", content.FirstParagraph);
    }

    [Fact]
    public async Task ParseAsync_skips_a_long_tag_list_that_is_not_a_real_paragraph()
    {
        // Regression: the length check alone let a run of keywords/tags through once it passed 80
        // chars, even though it reads nothing like a sentence (no articles, prepositions, etc.).
        const string html = """
            <html><body><article>
              <p>Colombia, Ecopetrol, Ambiente, Licencias, Produccion, Bucare, Petroleo, Economia, Energia, Internacional</p>
              <p>Este es un párrafo lo suficientemente largo como para ser considerado el primer párrafo real de la noticia completa.</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.StartsWith("Este es un párrafo lo suficientemente largo", content.FirstParagraph);
    }

    [Fact]
    public async Task ParseAsync_includes_every_qualifying_paragraph_in_FullText_for_keyword_matching()
    {
        // FirstParagraph stays short (lead sentence only, for the alert) but FullText must carry a
        // mention that only shows up several paragraphs into the story — see Ecopetrol/Hocol bug.
        const string html = """
            <html><body><article>
              <p>La primera fase contempla una inversión cercana a 6.300 millones y busca conectar mil hogares.</p>
              <p>Colgas, Hocol, empresa del Grupo Ecopetrol, y la Alcaldía de Tierralta pusieron en operación la red.</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal("La primera fase contempla una inversión cercana a 6.300 millones y busca conectar mil hogares.", content.FirstParagraph);
        Assert.Contains("Ecopetrol", content.FullText);
        Assert.Contains("primera fase", content.FullText);
    }

    [Fact]
    public async Task ParseAsync_parses_a_free_form_spanish_date_with_month_name()
    {
        const string html = """
            <html><head>
              <meta name="date" content="2 de octubre de 2026" />
            </head><body></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }

    [Fact]
    public async Task ParseAsync_parses_a_short_numeric_month_abbreviation_spanish_date()
    {
        const string html = """
            <html><head>
              <meta name="pubdate" content="10 oct 2026" />
            </head><body></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }

    [Fact]
    public async Task ParseAsync_tries_the_next_date_candidate_when_the_first_one_fails_to_parse()
    {
        // Regression: the old code only moved to the next selector when the first was MISSING,
        // never when its text existed but failed to parse — so a bad value in the first tag hid a
        // perfectly good one in every tag checked after it.
        const string html = """
            <html><head>
              <meta property="article:published_time" content="no es una fecha" />
              <meta itemprop="datePublished" content="2026-10-02T09:00:00-05:00" />
            </head><body></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }

    [Fact]
    public async Task ParseAsync_reads_date_from_additional_known_meta_labels()
    {
        const string html = """
            <html><head>
              <meta name="sailthru.date" content="2026-10-02T09:00:00-05:00" />
            </head><body></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }

    [Fact]
    public async Task ParseAsync_reads_the_date_from_a_time_tags_visible_text_when_it_has_no_datetime_attribute()
    {
        const string html = """
            <html><body>
              <time>10 oct 2026</time>
            </body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }
}
