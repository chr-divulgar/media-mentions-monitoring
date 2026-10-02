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
}
