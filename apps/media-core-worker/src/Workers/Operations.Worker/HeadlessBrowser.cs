using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Reads a page's final HTML the way a real browser does (JavaScript included).</summary>
public interface IBrowserPageReader
{
    // Null when the page could not be loaded or never got past an anti-bot challenge.
    Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Prints a page to PDF the way a real browser does (the permanent copy of a web note, RQ-014).</summary>
public interface IBrowserPagePrinter
{
    // False when the page could not be loaded or never got past an anti-bot challenge. highlightTerms are marked in
    // yellow in the printed page, the way a browser's find does.
    Task<bool> PrintPdfAsync(
        string url, string outputPath, IReadOnlyList<string>? highlightTerms = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// One shared headless Chromium (Playwright) for everything that needs a real browser: today the
/// fallback for outlets behind an anti-bot challenge, later the permanent PDF copy of each note
/// (RQ-014, page.PdfAsync on the same browser).
///
/// Portable: Chromium is downloaded by Playwright itself into BrowsersPath (a folder of the worker,
/// not the machine's user profile) the first time it is needed — nothing installed on the host.
/// On Linux, Chromium's system libraries come with `playwright install --with-deps` in the image.
/// </summary>
public sealed class HeadlessBrowser : IBrowserPageReader, IBrowserPagePrinter, IAsyncDisposable
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    // A challenge that a real browser can pass resolves within seconds; past this it never will.
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(25);

    // Long enough for a page's own scripts and images, short enough that a page full of ad trackers (whose load event may
    // never come) does not stall the note: past this the page is printed as it is.
    private static readonly TimeSpan DefaultLoadWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConsentWait = TimeSpan.FromSeconds(3);

    // The consent managers outlets use (the accept button of each), and the buttons any other one labels that way.
    private const string ConsentButtonCss =
        "#didomi-notice-agree-button, #onetrust-accept-btn-handler, #CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll, .fc-cta-consent";
    private static readonly Regex ConsentButtonName = new(
        @"^\s*(agree and close|accept all( cookies)?|aceptar( todo| todas)?( y cerrar)?|acepto|estoy de acuerdo|entendido|permitir todas?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // What a consent wall leaves behind once (or if never) accepted: its overlay and the page lock it puts on the body.
    private const string HideConsentCss = @"
        #didomi-host, #didomi-popup, #onetrust-consent-sdk, #acceptationCMPWall, .cmp-overlay, .blockNavigation { display: none !important; }
        html, body { overflow: visible !important; position: static !important; }
        dialog[open], [popover], [role=dialog], [aria-modal=true], .elementor-popup-modal, .dialog-widget, .pum-overlay, .om-holder { display: none !important; }
        .tbl-read-more-box { display: none !important; }
        .tbl-forkorts-article { max-height: none !important; height: auto !important; overflow: visible !important; }
        ";

    // Windows, banners, a player stuck to the bottom, an ad that pops up (Google's vignette arrives seconds after the
    // page): whatever is fixed to the screen is not part of the note and would print over it. The one fixed container
    // that stays is the one holding the note's own title — a site whose whole page sits in a fixed box — and that is
    // the <h1> that matches the page's title, not just any <h1>: a newsletter window brings its own ("Inscríbete a
    // nuestro boletín"), and treating that as "the page" is what let it print over the note. It runs now and then every
    // quarter second for as long as the page is open, so what appears late (even while the PDF is being drawn) is
    // hidden too.
    private const string HidePinnedScript = @"
        () => {
          if (window.__hidePinned) { window.__hidePinned(); return; }
          const fold = t => (t || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/\s+/g, ' ').trim();
          const noteHeading = () => {
            const heads = [...document.querySelectorAll('h1')].filter(h => !h.closest('form, dialog, [role=dialog], [aria-modal=true], [popover]'));
            const title = fold(document.querySelector('meta[property=""og:title""]')?.content || document.title);
            const matches = h => { const t = fold(h.textContent); return t.length > 8 && title.length > 8 && (title.includes(t.slice(0, 40)) || t.includes(title.slice(0, 40))); };
            return heads.find(matches) || heads[0] || null;
          };
          window.__hidePinned = () => {
            const note = noteHeading();
            for (const el of document.querySelectorAll('body *')) {
              if (getComputedStyle(el).position !== 'fixed') continue;
              const holdsNote = note ? (el === note || el.contains(note)) : (el.matches('article') || !!el.querySelector('article'));
              if (!holdsNote) el.style.setProperty('display', 'none', 'important');
            }
          };
          window.__hidePinned();
          setInterval(window.__hidePinned, 250);
        }
        ";

    // A box that cuts the article short with a height limit and a ""Leer más"" button (Taboola's Read More on many outlets,
    // or a site's own): the whole text is what has to be printed, so the limit goes and so does the button. It is not
    // clicked: many of these buttons are links to another page. Only boxes that hide a good amount of real paragraph text
    // are opened, so a carousel or a menu is left alone.
    private const string ExpandClippedScript = @"
        () => {
          const readMore = /^(leer m[aá]s|seguir leyendo|continuar leyendo|ver m[aá]s|ver todo|read more)\s*[»>→↓⌄]*$/i;
          for (const el of document.querySelectorAll('body *')) {
            const cs = getComputedStyle(el);
            if (el === document.body || el === document.documentElement || cs.position === 'fixed') continue;
            const clipped = (cs.overflow === 'hidden' || cs.overflowY === 'hidden') && el.scrollHeight > el.clientHeight + 150;
            if (!clipped) continue;
            const text = [...el.querySelectorAll('p')].reduce((n, p) => n + p.textContent.trim().length, 0);
            if (text < 400) continue;
            for (const prop of ['max-height', 'height', 'min-height']) el.style.setProperty(prop, prop === 'min-height' ? '0' : 'none', 'important');
            el.style.setProperty('height', 'auto', 'important');
            el.style.setProperty('overflow', 'visible', 'important');
          }
          for (const el of document.querySelectorAll('a, button, [role=button], span, div')) {
            if (el.children.length < 3 && readMore.test((el.textContent || '').trim())) el.style.setProperty('display', 'none', 'important');
          }
        }
        ";

    // Lazy images only load when they are scrolled to.
    private const string ScrollThroughScript = @"
        async () => {
          const end = Date.now() + 3000;
          for (let y = 0; y < document.documentElement.scrollHeight && Date.now() < end; y += window.innerHeight) {
            window.scrollTo(0, y);
            await new Promise(resolve => setTimeout(resolve, 150));
          }
          window.scrollTo(0, 0);
        }
        ";

    private readonly string browsersPath;
    private readonly TimeSpan loadWait;
    private readonly ILogger<HeadlessBrowser> logger;
    private readonly SemaphoreSlim startGate = new(1, 1);
    // A browser page is heavy; a few at a time is plenty for the occasional blocked outlet.
    private readonly SemaphoreSlim pageGate = new(2, 2);
    private IPlaywright? playwright;
    private IBrowser? browser;

    public HeadlessBrowser(string browsersPath, ILogger<HeadlessBrowser> logger, TimeSpan? loadWait = null)
    {
        this.browsersPath = browsersPath;
        this.logger = logger;
        this.loadWait = loadWait ?? DefaultLoadWait;
    }

    public async Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        await pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IBrowserContext? context = null;
        try
        {
            context = await NewContextAsync(cancellationToken).ConfigureAwait(false);
            var page = await OpenPageAsync(context, url, WaitUntilState.DOMContentLoaded).ConfigureAwait(false);
            return page is null ? null : await page.ContentAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[HeadlessBrowser] Could not load {Url}.", url);
            return null;
        }
        finally
        {
            if (context is not null)
            {
                await context.CloseAsync().ConfigureAwait(false);
            }

            pageGate.Release();
        }
    }

    public async Task<bool> PrintPdfAsync(
        string url, string outputPath, IReadOnlyList<string>? highlightTerms = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithPreparedPageAsync(
                url,
                async page =>
                {
                    if (highlightTerms is { Count: > 0 })
                    {
                        await HighlightTermsAsync(page, highlightTerms).ConfigureAwait(false);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                    await page.PdfAsync(new PagePdfOptions { Path = outputPath, Format = "A4", PrintBackground = true }).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[HeadlessBrowser] Could not print {Url} to PDF.", url);
            return false;
        }
    }

    // Opens the page the way the note's PDF needs it (loaded as far as it will, consent wall out of the way) and hands
    // it to use. Default when the page could not be opened (an anti-bot challenge it never passed).
    internal async Task<T?> WithPreparedPageAsync<T>(string url, Func<IPage, Task<T>> use, CancellationToken cancellationToken)
    {
        await pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IBrowserContext? context = null;
        try
        {
            context = await NewContextAsync(cancellationToken).ConfigureAwait(false);
            // Not "load": a page of ad trackers may never fire it (Caracol did not in 30 s). The content is there once
            // the DOM is; load is waited for a while only to get the images and styles, then the page is printed as it is.
            var page = await OpenPageAsync(context, url, WaitUntilState.DOMContentLoaded).ConfigureAwait(false);
            if (page is null)
            {
                return default;
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = (float)loadWait.TotalMilliseconds }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogInformation("[HeadlessBrowser] {Url} did not finish loading in {Seconds}s; using it as it is.", url, loadWait.TotalSeconds);
            }

            await PrepareForPrintAsync(page).ConfigureAwait(false);
            return await use(page).ConfigureAwait(false);
        }
        finally
        {
            if (context is not null)
            {
                await context.CloseAsync().ConfigureAwait(false);
            }

            pageGate.Release();
        }
    }

    // Marks every occurrence of the terms in the page's text in yellow, as a browser's find does: any case, accents
    // ignored ("arbol" finds "Árbol"), as part of a word too. Script and style text, form fields and what is already
    // marked are left alone. Returns how many marks were made (capped, so a one-letter term cannot freeze a page).
    internal static async Task<int> HighlightTermsAsync(IPage page, IReadOnlyList<string> terms)
    {
        var pattern = HighlightPattern(terms);
        return pattern is null ? 0 : await page.EvaluateAsync<int>(HighlightScript, pattern).ConfigureAwait(false);
    }

    private const string AccentedForms = "aàáâãäåāăą|eèéêëēėę|iìíîïīį|oòóôõöøō|uùúûüūů|nñ|cçćč";

    // One JavaScript regular expression for all the terms (longest first, so "Ecopetrol S.A." wins over "Ecopetrol"):
    // each letter also matches its accented forms and spaces match any run of whitespace. Null when there is no term.
    internal static string? HighlightPattern(IReadOnlyList<string> terms)
    {
        var alternatives = new List<string>();
        foreach (var term in terms.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term.Trim()).Distinct().OrderByDescending(term => term.Length))
        {
            var pattern = new System.Text.StringBuilder();
            foreach (var character in term.Normalize(System.Text.NormalizationForm.FormD)
                         .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark))
            {
                var forms = AccentedForms.Split('|').FirstOrDefault(group => group[0] == char.ToLowerInvariant(character));
                if (char.IsWhiteSpace(character))
                {
                    pattern.Append(@"\s+");
                }
                else if (forms is not null)
                {
                    pattern.Append('[').Append(forms).Append(forms.ToUpperInvariant()).Append(']');
                }
                else
                {
                    // Only the characters a pattern gives a meaning to are escaped (JavaScript's unicode mode rejects any other escape).
                    pattern.Append(@"\^$.*+?()[]{}|/".Contains(character) ? "\\" : string.Empty).Append(character);
                }
            }

            if (pattern.Length > 0)
            {
                alternatives.Add(pattern.ToString());
            }
        }

        return alternatives.Count == 0 ? null : string.Join('|', alternatives);
    }

    private const string HighlightScript = @"
        (pattern) => {
          const finder = new RegExp(pattern, 'giu');
          const tester = new RegExp(pattern, 'iu');
          const skip = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEXTAREA', 'INPUT', 'SELECT', 'MARK', 'TEMPLATE']);
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode: node => node.parentElement && !skip.has(node.parentElement.tagName) && tester.test(node.nodeValue)
              ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT
          });
          const nodes = [];
          while (walker.nextNode()) nodes.push(walker.currentNode);

          let marks = 0;
          for (const node of nodes) {
            if (marks >= 2000) break;
            const text = node.nodeValue;
            const fragment = document.createDocumentFragment();
            let last = 0;
            finder.lastIndex = 0;
            let match;
            while ((match = finder.exec(text)) && match[0].length > 0) {
              fragment.append(text.slice(last, match.index));
              const mark = document.createElement('mark');
              mark.textContent = match[0];
              mark.style.cssText = 'background:#ffff00 !important;color:#000 !important;padding:0 !important;-webkit-print-color-adjust:exact !important;print-color-adjust:exact !important;';
              fragment.append(mark);
              last = match.index + match[0].length;
              marks++;
            }
            fragment.append(text.slice(last));
            node.parentNode.replaceChild(fragment, node);
          }
          return marks;
        }
        ";

    // Accepts the cookie wall if there is one (a few seconds at most: many appear after the page has loaded), then hides
    // what remains of it and of anything fixed to the screen, and scrolls through the page so lazy images load. Every
    // step is best effort: a page without a wall just goes on.
    private async Task PrepareForPrintAsync(IPage page)
    {
        try
        {
            var accept = page.Locator(ConsentButtonCss).Or(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = ConsentButtonName })).First;
            try
            {
                await accept.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = (float)ConsentWait.TotalMilliseconds }).ConfigureAwait(false);
                await accept.ClickAsync(new LocatorClickOptions { Timeout = 2000 }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or PlaywrightException)
            {
                // No wall, or one that is not a known kind: it is hidden below.
            }

            await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = HideConsentCss }).ConfigureAwait(false);
            await page.EvaluateAsync(HidePinnedScript).ConfigureAwait(false);
            await page.EvaluateAsync(ExpandClippedScript).ConfigureAwait(false);
            await page.EvaluateAsync(ScrollThroughScript).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(1000).ConfigureAwait(false); // what the scroll (or a late ad) brings up

            // The last sweep is made as the page will be printed: a window that is only fixed (or only there) in the print
            // style, or that came in after the first sweep, goes too; the page is then printed in that same style.
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print }).ConfigureAwait(false);
            await page.EvaluateAsync(HidePinnedScript).ConfigureAwait(false);
            await page.EvaluateAsync(ExpandClippedScript).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            logger.LogWarning(exception, "[HeadlessBrowser] Could not tidy the page before printing; printing it as it is.");
        }
    }

    private async Task<IBrowserContext> NewContextAsync(CancellationToken cancellationToken)
    {
        var running = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
        return await running.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "es-CO" }).ConfigureAwait(false);
    }

    // Null when the page never got past an anti-bot challenge.
    private async Task<IPage?> OpenPageAsync(IBrowserContext context, string url, WaitUntilState waitUntil)
    {
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = waitUntil,
            Timeout = (float)NavigationTimeout.TotalMilliseconds,
        }).ConfigureAwait(false);

        // Cloudflare's interstitial ("Just a moment...") replaces itself with the real page once
        // its script has checked the browser; wait for that rather than reading the interstitial.
        var deadline = DateTime.UtcNow + ChallengeWait;
        while (IsChallengeTitle(await page.TitleAsync().ConfigureAwait(false)))
        {
            if (DateTime.UtcNow > deadline)
            {
                logger.LogWarning("[HeadlessBrowser] Still on the anti-bot challenge after {Seconds}s: {Url}", ChallengeWait.TotalSeconds, url);
                return null;
            }

            await page.WaitForTimeoutAsync(1000).ConfigureAwait(false);
        }

        return page;
    }

    internal static bool IsChallengeTitle(string? title) =>
        title is not null && title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);

    // Started on first use (and again if the browser process died): most cycles never need it, and
    // the first start also downloads Chromium's headless shell (~275 MB on disk; it also renders
    // PDFs) if this folder does not have it yet.
    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (browser is { IsConnected: true })
        {
            return browser;
        }

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (browser is { IsConnected: true })
            {
                return browser;
            }

            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
            var installExit = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "--only-shell", "chromium"]), cancellationToken).ConfigureAwait(false);
            if (installExit != 0)
            {
                throw new InvalidOperationException($"Playwright could not install Chromium into '{browsersPath}' (exit {installExit}).");
            }

            playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-blink-features=AutomationControlled"],
            }).ConfigureAwait(false);

            logger.LogInformation("[HeadlessBrowser] Chromium started (browsers in '{Path}').", browsersPath);
            return browser;
        }
        finally
        {
            startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }

        playwright?.Dispose();
    }
}
