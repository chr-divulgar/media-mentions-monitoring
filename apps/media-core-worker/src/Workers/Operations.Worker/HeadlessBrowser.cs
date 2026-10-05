using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Reads a page's final HTML the way a real browser does (JavaScript included).</summary>
public interface IBrowserPageReader
{
    // Null when the page could not be loaded or never got past an anti-bot challenge.
    Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default);
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
public sealed class HeadlessBrowser : IBrowserPageReader, IAsyncDisposable
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    // A challenge that a real browser can pass resolves within seconds; past this it never will.
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(25);

    private readonly string browsersPath;
    private readonly ILogger<HeadlessBrowser> logger;
    private readonly SemaphoreSlim startGate = new(1, 1);
    // A browser page is heavy; a few at a time is plenty for the occasional blocked outlet.
    private readonly SemaphoreSlim pageGate = new(2, 2);
    private IPlaywright? playwright;
    private IBrowser? browser;

    public HeadlessBrowser(string browsersPath, ILogger<HeadlessBrowser> logger)
    {
        this.browsersPath = browsersPath;
        this.logger = logger;
    }

    public async Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        await pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IBrowserContext? context = null;
        try
        {
            var running = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
            context = await running.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "es-CO" }).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
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

            return await page.ContentAsync().ConfigureAwait(false);
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
