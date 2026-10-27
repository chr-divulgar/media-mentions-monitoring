using System.Net;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The PDF copy of a web note must show the note: not the cookie-consent wall many outlets put over it, and not fail
// because a page full of ad trackers never finishes loading. These run a real headless Chromium against a local server
// with pages built like those outlets' (a Didomi-style wall, an image that never answers).
public sealed class HeadlessBrowserPrintTests : IAsyncLifetime
{
    private const string Article = "<article id=\"nota\"><h1>Titulo de la nota</h1><p>Texto de la nota que debe verse en el PDF.</p></article>";

    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"browser-print-{Guid.NewGuid():N}");
    private HeadlessBrowser? browser;
    private string baseUrl = string.Empty;

    // The Chromium the worker downloads into <worker>/bin/ms-playwright; without it these tests have nothing to run on.
    private static string? FindBrowsersPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "bin", "ms-playwright");
            if (Directory.Exists(candidate) && Directory.GetDirectories(candidate, "chromium_headless_shell-*").Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        var port = new Random().Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        baseUrl = $"http://127.0.0.1:{port}";
        _ = Task.Run(Serve);

        if (FindBrowsersPath() is { } path)
        {
            browser = new HeadlessBrowser(path, NullLogger<HeadlessBrowser>.Instance, loadWait: TimeSpan.FromSeconds(2));
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        stop.Cancel();
        listener.Close();
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        Directory.Delete(folder, recursive: true);
    }

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/slow")
                {
                    // Never answers: the page's load event never fires.
                    try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
                    return;
                }

                var html = path switch
                {
                    "/known" => $"<html><body>{Article}<div id=\"didomi-popup\" style=\"position:fixed;inset:0;background:#000;z-index:99999\">" +
                                "<button id=\"didomi-notice-agree-button\" onclick=\"document.getElementById('didomi-popup').remove()\">Agree and close</button></div></body></html>",
                    "/unknown" => $"<html><body style=\"overflow:hidden\">{Article}<div class=\"x\" style=\"position:fixed;inset:0;background:#000;z-index:99999\">Aceptas las cookies de 1044 socios</div></body></html>",
                    // A newsletter window with its own <h1> (Elementor popups do this), before the note in the page, over a veil.
                    "/popup" => "<html><head><title>Titulo de la nota | Medio</title></head><body>" +
                                "<div id=\"veil\" style=\"position:fixed;inset:0;background:rgba(0,0,0,.5);z-index:9998\"></div>" +
                                "<div id=\"pop\" style=\"position:fixed;left:0;bottom:0;width:300px;height:300px;background:#013;z-index:9999\"><h1>Inscríbete a nuestro boletín diario</h1><form><input name=\"email\"></form></div>" +
                                $"{Article}</body></html>",
                    // The whole page inside a fixed container: that one is the page, not a window.
                    "/fixedpage" => $"<html><body><div style=\"position:fixed;inset:0;overflow:auto\">{Article}</div></body></html>",
                    "/dialog" => $"<html><body>{Article}<dialog id=\"d\"><h1>Suscríbete a las notificaciones</h1><p>Texto del aviso</p></dialog><script>document.getElementById('d').showModal()</script></body></html>",
                    // Taboola's "Read More": the article in a box with a height limit and a button that opens the rest.
                    "/clipped" => "<html><body><main class=\"tbl-forkorts-article\" style=\"max-height:150px;overflow:hidden\"><article id=\"nota\"><h1>Titulo de la nota</h1>" +
                                  string.Concat(Enumerable.Range(1, 12).Select(i => $"<p>Parrafo {i}: " + new string('x', 160) + "</p>")) +
                                  "<p id=\"last\">Ultimo parrafo del articulo</p></article></main><div class=\"tbl-read-more-box\"><a class=\"tbl-read-more-btn\">Leer Más</a></div></body></html>",
                    // The same with other class names and a "Seguir leyendo" link of the site's own.
                    "/clipped2" => "<html><body><div class=\"caja\" style=\"height:120px;overflow:hidden\"><article id=\"nota\"><h1>Titulo de la nota</h1>" +
                                   string.Concat(Enumerable.Range(1, 12).Select(i => $"<p>Parrafo {i}: " + new string('y', 160) + "</p>")) +
                                   "<p id=\"last\">Ultimo parrafo del articulo</p></article></div><a id=\"more\" href=\"#\">Seguir leyendo</a></body></html>",
                    "/words" => "<html><body><article id=\"nota\"><h1>Ecopetrol y el árbol</h1><p>El ARBOL de Ecopetrol S.A. creció, dijo la ecopetrolera.</p>" +
                                "<script id=\"s\">var ecopetrol = 'arbol';</script></article></body></html>",
                    "/hang" => $"<html><body>{Article}<img src=\"/slow\"></body></html>",
                    _ => $"<html><body>{Article}</body></html>",
                };
                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            });
        }
    }

    // What is on top of the article's own centre once the page is ready to print.
    private async Task<string> CenterOfPageAsync(string route)
    {
        return await browser!.WithPreparedPageAsync(
            baseUrl + route,
            page => page.EvaluateAsync<string>(
                "() => { const r = document.querySelector('article').getBoundingClientRect(); const e = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return e ? (e.closest('article') ? 'article' : e.tagName + '.' + e.className) : 'none'; }"),
            CancellationToken.None) ?? "page not opened";
    }

    [Theory]
    [InlineData("/known")]
    [InlineData("/unknown")]
    public async Task The_cookie_wall_is_gone_when_the_page_is_ready_to_print(string route)
    {
        if (browser is null) return; // no Chromium on this machine

        Assert.Equal("article", await CenterOfPageAsync(route));
    }

    private static readonly string[] Terms = ["ecopetrol", "arbol", "S.A."];

    [Fact]
    public async Task Keywords_are_highlighted_in_yellow_like_a_browser_search_ignoring_case_and_accents()
    {
        if (browser is null) return;

        var result = await browser.WithPreparedPageAsync(
            baseUrl + "/words",
            async page =>
            {
                var count = await HeadlessBrowser.HighlightTermsAsync(page, Terms);
                var marks = await page.EvaluateAsync<string[]>(
                    "() => [...document.querySelectorAll('mark')].map(m => m.textContent + '|' + getComputedStyle(m).backgroundColor)");
                var script = await page.EvaluateAsync<string>("() => document.getElementById('s').textContent");
                return (count, marks, script);
            },
            CancellationToken.None);

        // Ecopetrol (title), árbol, ARBOL, Ecopetrol, S.A., the "ecopetrol" inside ecopetrolera: a substring match, as a find does.
        Assert.Equal(6, result.count);
        Assert.Equal(6, result.marks.Length);
        Assert.All(result.marks, mark => Assert.EndsWith("|rgb(255, 255, 0)", mark));
        Assert.Contains("árbol|rgb(255, 255, 0)", result.marks);
        Assert.Contains("ARBOL|rgb(255, 255, 0)", result.marks);
        Assert.Contains("S.A.|rgb(255, 255, 0)", result.marks);
        // The text of the page is the same and the script was left alone.
        Assert.Equal("var ecopetrol = 'arbol';", result.script);
    }

    // Is the element showing, and is it the page's own (not covered)? What the tests below ask of a prepared page.
    private async Task<T?> InPreparedPageAsync<T>(string route, string script) =>
        await browser!.WithPreparedPageAsync(baseUrl + route, page => page.EvaluateAsync<T>(script), CancellationToken.None);

    private const string IsHidden = "(id) => { const e = document.getElementById(id); return !e || getComputedStyle(e).display === 'none'; }";

    [Fact]
    public async Task A_newsletter_window_with_its_own_h1_is_hidden_and_the_note_is_not()
    {
        if (browser is null) return;

        // The window is before the note and has an <h1> of its own: it used to count as "the page" and stay.
        Assert.Equal("article", await CenterOfPageAsync("/popup"));
        Assert.True(await InPreparedPageAsync<bool>("/popup", "() => ['pop', 'veil'].every(id => getComputedStyle(document.getElementById(id)).display === 'none')"));
    }

    [Fact]
    public async Task A_fixed_container_that_holds_the_note_stays()
    {
        if (browser is null) return;

        Assert.Equal("article", await CenterOfPageAsync("/fixedpage"));
    }

    [Fact]
    public async Task An_open_dialog_is_hidden()
    {
        if (browser is null) return;

        Assert.True(await InPreparedPageAsync<bool>("/dialog", "() => getComputedStyle(document.getElementById('d')).display === 'none'"));
    }

    [Theory]
    [InlineData("/clipped")]
    [InlineData("/clipped2")]
    public async Task An_article_cut_short_with_a_read_more_button_is_shown_whole_and_the_button_is_gone(string route)
    {
        if (browser is null) return;

        // The last paragraph is inside the page (not hidden by the box's height limit)...
        var visible = await InPreparedPageAsync<bool>(
            route,
            "() => { const last = document.getElementById('last').getBoundingClientRect(); let box = document.getElementById('last').parentElement; " +
            "while (box && getComputedStyle(box).overflow !== 'hidden') box = box.parentElement; " +
            "return !box || box.getBoundingClientRect().bottom >= last.bottom - 1; }");
        Assert.True(visible);
        // ...and the button that was to open the rest is not printed.
        Assert.True(await InPreparedPageAsync<bool>(
            route,
            "() => [...document.querySelectorAll('a')].filter(a => /leer m|seguir leyendo/i.test(a.textContent)).every(a => a.offsetParent === null)"));
    }

    [Fact]
    public async Task Highlighting_with_no_terms_changes_nothing_and_a_pdf_is_printed_with_them()
    {
        if (browser is null) return;

        var none = await browser.WithPreparedPageAsync(baseUrl + "/words", page => HeadlessBrowser.HighlightTermsAsync(page, []), CancellationToken.None);
        Assert.Equal(0, none);

        var pdf = Path.Combine(folder, "words.pdf");
        Assert.True(await browser.PrintPdfAsync(baseUrl + "/words", pdf, Terms));
        Assert.True(new FileInfo(pdf).Length > 1000);
    }

    [Fact]
    public async Task A_page_that_never_finishes_loading_is_still_printed()
    {
        if (browser is null) return;
        var pdf = Path.Combine(folder, "hang.pdf");

        var printed = await browser.PrintPdfAsync(baseUrl + "/hang", pdf);

        Assert.True(printed);
        Assert.True(new FileInfo(pdf).Length > 1000);
    }

    [Fact]
    public async Task A_plain_page_is_printed_as_before()
    {
        if (browser is null) return;
        var pdf = Path.Combine(folder, "plain.pdf");

        Assert.True(await browser.PrintPdfAsync(baseUrl + "/plain", pdf));
        Assert.Equal("article", await CenterOfPageAsync("/plain"));
        Assert.True(new FileInfo(pdf).Length > 1000);
    }
}
