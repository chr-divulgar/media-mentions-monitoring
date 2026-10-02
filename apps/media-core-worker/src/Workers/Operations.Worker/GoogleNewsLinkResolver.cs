using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Resolves a Google News RSS article id to the outlet's real URL. Google News RSS links point at
/// an interstitial page (news.google.com/rss/articles/{id}) rather than the article itself; the
/// real URL is embedded in a signed payload that page carries and that a second, undocumented
/// Google endpoint (batchexecute) decodes. There is no public API for this — the shape is derived
/// from how news.google.com's own web client does it, and may need adjusting if Google changes it;
/// see ResolveAsync's doc comment for the fallback if it ever breaks outright.
/// </summary>
public sealed partial class GoogleNewsLinkResolver : INewsLinkResolver
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";
    private const string BatchExecuteUrl = "https://news.google.com/_/DotsSplashUi/data/batchexecute";

    private readonly HttpClient httpClient;
    private readonly int requestTimeoutSeconds;
    private readonly ILogger<GoogleNewsLinkResolver> logger;

    public GoogleNewsLinkResolver(HttpClient httpClient, int requestTimeoutSeconds, ILogger<GoogleNewsLinkResolver> logger)
    {
        this.httpClient = httpClient;
        this.requestTimeoutSeconds = requestTimeoutSeconds;
        this.logger = logger;
    }

    // If this starts failing for every article, Google most likely changed the page or the
    // batchexecute payload shape — the fix is a paid news-search API behind this same interface,
    // not a repair here (see PLAN_MAESTRO.md risk notes for this feature).
    public async Task<NewsLinkResolution> ResolveAsync(NewsSearchItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await GetAsync($"https://news.google.com/rss/articles/{item.GoogleId}", cancellationToken).ConfigureAwait(false);

            var signature = SignatureAttribute().Match(html);
            var timestamp = TimestampAttribute().Match(html);
            if (!signature.Success || !timestamp.Success)
            {
                return new NewsLinkResolution(null, "Google no entregó la firma del enlace (puede haber cambiado el formato)");
            }

            var responseText = await PostBatchExecuteAsync(item.GoogleId, timestamp.Groups[1].Value, signature.Groups[1].Value, cancellationToken)
                .ConfigureAwait(false);

            var url = ParseBatchExecuteUrl(responseText);
            return url is null
                ? new NewsLinkResolution(null, "No se pudo leer la URL real en la respuesta de Google")
                : new NewsLinkResolution(url, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NewsLinkResolution(null, $"Tiempo agotado resolviendo el enlace ({requestTimeoutSeconds}s)");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[GoogleNewsLinkResolver] Failed to resolve {GoogleId}.", item.GoogleId);
            return new NewsLinkResolution(null, exception.Message);
        }
    }

    private async Task<string> PostBatchExecuteAsync(string googleId, string timestamp, string signature, CancellationToken cancellationToken)
    {
        // Mirrors the payload news.google.com's own web client sends for "get the real URL behind
        // this article id" (action "Fbv4je" / "garturlreq"). The embedded array's values beyond
        // the id/timestamp/signature are fixed client-identity fields Google expects verbatim.
        var innerPayload = JsonSerializer.Serialize(new object?[]
        {
            "garturlreq",
            new object?[]
            {
                new object?[] { "X", "X", new object?[] { "X", "X" }, null, null, 1, 1, "US:en", null, 1, null, null, null, null, null, 0, 1 },
                "X", "X", 1, new object?[] { 1, 1, 1 }, 1, 1, null, 0, 0, null, 0,
            },
            googleId,
            long.Parse(timestamp),
            signature,
        });
        var freq = JsonSerializer.Serialize(new object?[] { new object?[] { new object?[] { "Fbv4je", innerPayload } } });

        using var request = new HttpRequestMessage(HttpMethod.Post, BatchExecuteUrl)
        {
            Content = new StringContent("f.req=" + Uri.EscapeDataString(freq), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        // The default HttpClient follows the redirect from /rss/articles/{id} to the page that
        // actually carries the signature attributes, with no special handling needed here.
        using var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
    }

    // Internal so the exact batchexecute chunked-JSON shape can be covered with a saved fixture.
    internal static string? ParseBatchExecuteUrl(string responseText)
    {
        var parts = responseText.Split("\n\n", 2, StringSplitOptions.None);
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var outerDocument = JsonDocument.Parse(parts[1]);
            var root = outerDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            var entry = root[0];
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 3)
            {
                return null;
            }

            var payload = entry[2].GetString();
            if (payload is null)
            {
                return null;
            }

            using var payloadDocument = JsonDocument.Parse(payload);
            var payloadArray = payloadDocument.RootElement;
            return payloadArray.ValueKind == JsonValueKind.Array && payloadArray.GetArrayLength() > 1
                ? payloadArray[1].GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("data-n-a-sg=\"([^\"]+)\"")]
    private static partial Regex SignatureAttribute();

    [GeneratedRegex("data-n-a-ts=\"([^\"]+)\"")]
    private static partial Regex TimestampAttribute();
}
