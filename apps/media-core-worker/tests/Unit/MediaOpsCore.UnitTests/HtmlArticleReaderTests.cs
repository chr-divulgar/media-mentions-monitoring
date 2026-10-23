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

    [Theory]
    // Regression: TradingView's lead "Ecopetrol S.A. (BVC:ECOPETROL; NYSE:EC) anunció hoy..." was cut to "Ecopetrol S.A.".
    [InlineData(
        "Ecopetrol S.A. (BVC:ECOPETROL; NYSE:EC) anunció hoy que su Consejo de Administración designó a Armando José Cuello. Otra frase más.",
        "Ecopetrol S.A. (BVC:ECOPETROL; NYSE:EC) anunció hoy que su Consejo de Administración designó a Armando José Cuello.")]
    [InlineData(
        "La firma Grupo Sura S.A.S. Colombia reportó ganancias en el trimestre para la región. Analistas esperan más.",
        "La firma Grupo Sura S.A.S. Colombia reportó ganancias en el trimestre para la región.")]
    [InlineData(
        "El Dr. García y la Sra. Pérez, de la Cía. Minera Ltda. de la región, firmaron el contrato en Bogotá. Luego hablaron.",
        "El Dr. García y la Sra. Pérez, de la Cía. Minera Ltda. de la región, firmaron el contrato en Bogotá.")]
    [InlineData(
        "La producción subió 3.5 % en EE.UU. durante el mes de septiembre según la agencia. Analistas lo esperaban.",
        "La producción subió 3.5 % en EE.UU. durante el mes de septiembre según la agencia.")]
    public async Task ParseAsync_does_not_end_the_lead_sentence_at_an_abbreviation(string paragraph, string expected)
    {
        var html = $"<html><body><article><p>{paragraph}</p></article></body></html>";

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(expected, content.FirstParagraph);
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
    public async Task ParseAsync_excludes_a_paragraph_that_is_really_a_related_content_teaser_link()
    {
        // Regression: a "related articles" carousel renders its title/date as <p>s inside an <a> —
        // real body prose is never link text, so this must not be read as the article's own text,
        // even if a teaser title happens to mention a client by name (e.g. a pun headline).
        const string html = """
            <html><body><article>
              <div class="wrap-teaser-caricatura">
                <a href="/otra-nota">
                  <p class="lp-caricatura-title">Caricatura de hoy sobre la situación de Ecopetrol y su futuro en el país entero</p>
                  <p class="lp-caricatura-date">01 Oct, 2026</p>
                </a>
              </div>
              <p>Este es el párrafo real de la noticia actual, lo suficientemente largo como para contar.</p>
            </article></body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.StartsWith("Este es el párrafo real", content.FirstParagraph);
        Assert.DoesNotContain("Ecopetrol", content.FullText);
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

    [Theory]
    [InlineData("Publicado el 06/10/2026 por la redacción", 2026, 10, 6)]
    [InlineData("Publicado el 6 de octubre de 2026", 2026, 10, 6)]
    [InlineData("Publicado 2026-10-06", 2026, 10, 6)]
    public async Task ParseAsync_scans_page_text_as_last_resort_and_flags_it(string text, int year, int month, int day)
    {
        var html = $"<html><body><div><script>var d='01/01/2000';</script><b>{text}</b></div></body></html>";

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
        Assert.Equal("page-text-scan", content.PublishedAtSource);
    }

    [Fact]
    public async Task ParseAsync_leaves_the_source_null_when_a_conventional_date_is_found()
    {
        var content = await HtmlArticleReader.ParseAsync("<html><body><time datetime=\"2026-10-06T08:00:00-05:00\"></time></body></html>");

        Assert.Null(content.PublishedAtSource);
    }

    [Fact]
    public async Task ParseAsync_reads_the_rdfa_dc_date_content_attribute()
    {
        const string html = """
            <html><body>
              <span property="dc:date" datatype="xsd:dateTime" content="2026-10-05T16:17:00-05:00" class="date-display-single">Lunes 05 de Octubre de 2026 - 4:17pm</span>
            </body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 16, 17, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
        Assert.Null(content.PublishedAtSource);
    }

    [Fact]
    public async Task ParseAsync_reads_the_date_from_a_span_with_class_date()
    {
        const string html = """
            <html><body>
              <p class="post-meta"><span class="date"><i class="fa fa-clock-o"></i>
                06 Oct 2026  </span></p>
            </body></html>
            """;

        var content = await HtmlArticleReader.ParseAsync(html);

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.FromHours(-5)), content.PublishedAt);
    }

    [Fact]
    public void FindClientRedirect_reads_the_tco_style_stub()
    {
        // Verbatim from https://t.co/... — the page the reader got instead of the article.
        const string stub = """
            <head><noscript><META http-equiv="refresh" content="0;URL=https://runrunpolitico.com/noticias/consejo/"></noscript><title>https://runrunpolitico.com/noticias/consejo/</title></head><script>window.opener = null; location.replace("https:\/\/runrunpolitico.com\/noticias\/consejo\/")</script>
            """;

        Assert.Equal("https://runrunpolitico.com/noticias/consejo/", HtmlArticleReader.FindClientRedirect(stub, new Uri("https://t.co/abc")));
        Assert.Equal(
            "https://runrunpolitico.com/noticias/consejo/",
            HtmlArticleReader.FindClientRedirect(@"<script>location.replace(""https:\/\/runrunpolitico.com\/noticias\/consejo\/"")</script>", null));
    }

    [Fact]
    public void FindClientRedirect_ignores_real_pages()
    {
        var article = "<html><head><meta http-equiv=\"refresh\" content=\"0;URL=https://x.co/\"></head><body>" + new string('a', 5000) + "</body></html>";

        Assert.Null(HtmlArticleReader.FindClientRedirect(article, null));
    }
}
