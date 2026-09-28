using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Hosted service exposing worker-side HTTP routes on http://localhost:5000:
///   POST /youtube/cookies — receives fresh cookies from NestJS, validates them, writes them to
///     disk, clears the auth alert, and triggers an immediate reconciliation attempt.
///   GET  /youtube/health   — reports auth alert / cookie validity / excluded-source state.
///     The HTTP 200 response itself is the liveness signal callers are checking for.
///   GET  /capture/status   — reports per-source, per-hour capture coverage for one day (see
///     CaptureStatusSnapshotProvider), consumed by the web-ui capture status page.
///   GET  /audio/segment    — serves a closed-hour audio segment (mp3/wav) for Alerts' audio-edit
///     flow, consumed by NestJS's AudioService instead of it reading the worker's local D:\...
///     opus paths directly (see IClosedHourAudioReader).
///   GET  /whatsapp/status  — relays the WhatsApp sidecar's own /status (connected/pending_qr/
///     disconnected), consumed by NestJS's WhatsAppService/web-ui's status tab.
///   GET  /whatsapp/qr      — relays the WhatsApp sidecar's own /qr (base64 PNG or null).
/// </summary>
public sealed class YouTubeCookiesHttpService : IHostedService
{
    // Every other JSON boundary in this codebase uses camelCase (see FileSystemEvidenceStore,
    // JsonFileCaptureSourceRepository, etc.) — NestJS consumers expect the same convention.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Encoding.UTF8's preamble (BOM) prepended to the Netscape cookie file corrupts its
    // required "# Netscape HTTP Cookie File" header line, making yt-dlp reject the entire
    // file ("skipping cookie file entry due to invalid length 1: '﻿# Netscape...'").
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<YouTubeCookiesHttpService> logger;
    private readonly OperationsWorkerOptions options;
    private readonly IYouTubeCookiesValidator cookiesValidator;
    private readonly IYouTubeCookiesAlertService alertService;
    private readonly IYouTubeHealthSnapshotProvider healthSnapshotProvider;
    private readonly IYouTubeReconciliationTrigger reconciliationTrigger;
    private readonly ICaptureStatusSnapshotProvider captureStatusSnapshotProvider;
    private readonly IClosedHourAudioReader closedHourAudioReader;
    private readonly HttpClient httpClient;
    private readonly int whatsAppSidecarPort;
    private HttpListener? httpListener;
    private CancellationTokenSource? cts;

    public YouTubeCookiesHttpService(
        ILogger<YouTubeCookiesHttpService> logger,
        OperationsWorkerOptions options,
        IYouTubeCookiesValidator cookiesValidator,
        IYouTubeCookiesAlertService alertService,
        IYouTubeHealthSnapshotProvider healthSnapshotProvider,
        IYouTubeReconciliationTrigger reconciliationTrigger,
        ICaptureStatusSnapshotProvider captureStatusSnapshotProvider,
        IClosedHourAudioReader closedHourAudioReader,
        HttpClient httpClient)
    {
        this.logger = logger;
        this.options = options;
        this.cookiesValidator = cookiesValidator;
        this.alertService = alertService;
        this.healthSnapshotProvider = healthSnapshotProvider;
        this.reconciliationTrigger = reconciliationTrigger;
        this.captureStatusSnapshotProvider = captureStatusSnapshotProvider;
        this.closedHourAudioReader = closedHourAudioReader;
        this.httpClient = httpClient;
        whatsAppSidecarPort = options.WhatsAppSidecarPort;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Guards against a stray double-start; not expected in practice since host.StartAsync()
        // now starts every hosted service exactly once, early in Program.cs.
        if (httpListener?.IsListening == true)
        {
            return Task.CompletedTask;
        }

        try
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add("http://localhost:5000/");
            httpListener.Start();

            logger.LogInformation("[YouTubeCookiesHttpService] Started listening on http://localhost:5000");

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _ = Task.Run(async () => await HandleRequests(cts.Token), cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[YouTubeCookiesHttpService] Failed to start HTTP service");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            cts?.Cancel();
            httpListener?.Stop();
            httpListener?.Close();

            logger.LogInformation("[YouTubeCookiesHttpService] Stopped HTTP service");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[YouTubeCookiesHttpService] Error stopping HTTP service");
        }

        return Task.CompletedTask;
    }

    private async Task HandleRequests(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && httpListener?.IsListening == true)
        {
            try
            {
                var context = await httpListener.GetContextAsync();
                _ = Task.Run(async () => await HandleRequest(context), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // Expected when listener is closed
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[YouTubeCookiesHttpService] Error handling request");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            var method = context.Request.HttpMethod;
            var path = context.Request.Url?.AbsolutePath;

            if (method == "POST" && path == "/youtube/cookies")
            {
                await HandleCookiesSubmissionAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/youtube/health")
            {
                await HandleHealthRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/capture/status")
            {
                await HandleCaptureStatusRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/audio/segment")
            {
                await HandleAudioSegmentRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/whatsapp/status")
            {
                await RelayToSidecarAsync(context, "/status").ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/whatsapp/qr")
            {
                await RelayToSidecarAsync(context, "/qr").ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[YouTubeCookiesHttpService] Error handling YouTube cookies request");
            try
            {
                var errorResponse = new YouTubeCookiesResponse
                {
                    Success = false,
                    Message = $"Internal error: {ex.Message}",
                };
                await SendJsonResponse(context, 500, errorResponse).ConfigureAwait(false);
            }
            catch (Exception responseEx)
            {
                logger.LogError(responseEx, "[YouTubeCookiesHttpService] Failed to send error response");
            }
        }
    }

    private async Task HandleCookiesSubmissionAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        YouTubeCookiesRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<YouTubeCookiesRequest>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[YouTubeCookiesHttpService] Invalid JSON in request");
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Cookies))
        {
            logger.LogWarning("[YouTubeCookiesHttpService] Received empty or null cookies");
            await SendJsonResponse(context, 400, new YouTubeCookiesResponse
            {
                Success = false,
                Message = "Missing or empty 'cookies' field",
            }).ConfigureAwait(false);
            return;
        }

        var (statusCode, response) = await ProcessCookiesSubmissionAsync(request.Cookies, CancellationToken.None)
            .ConfigureAwait(false);
        await SendJsonResponse(context, statusCode, response).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates, writes, and reacts to a new cookies submission. Extracted from the raw
    /// <see cref="HttpListenerContext"/> handling so it can be unit tested directly.
    /// </summary>
    public async Task<(int StatusCode, YouTubeCookiesResponse Response)> ProcessCookiesSubmissionAsync(
        string cookiesContent, CancellationToken cancellationToken)
    {
        var cookiesFilePath = options.YoutubeCookiesFilePath;
        if (string.IsNullOrWhiteSpace(cookiesFilePath))
        {
            logger.LogError("[YouTubeCookiesHttpService] YoutubeCookiesFilePath not configured");
            return (500, new YouTubeCookiesResponse { Success = false, Message = "Worker configuration error" });
        }

        var validation = await cookiesValidator
            .ValidateCookiesAsync(cookiesFilePath: null, cookiesContent: cookiesContent)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            logger.LogWarning("[YouTubeCookiesHttpService] Rejected cookies submission: {Message}", validation.Message);
            return (422, new YouTubeCookiesResponse
            {
                Success = false,
                Message = validation.Message ?? "Cookies failed validation",
            });
        }

        var absoluteCookiesPath = ResolveAbsolutePath(cookiesFilePath);
        var cookiesDir = Path.GetDirectoryName(absoluteCookiesPath);
        if (!string.IsNullOrWhiteSpace(cookiesDir))
        {
            Directory.CreateDirectory(cookiesDir);
        }

        await File.WriteAllTextAsync(absoluteCookiesPath, cookiesContent, Utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("[YouTubeCookiesHttpService] Cookies saved to: {Path}", absoluteCookiesPath);

        alertService.ClearAlert();

        // Fire-and-forget: recovering excluded sources can involve several sequential yt-dlp
        // calls (up to YtdlpResolutionTimeoutSeconds each), which would make the caller's HTTP
        // request wait far longer than its own timeout budget. The response below reports that
        // cookies were accepted; recovery happens in the background.
        _ = Task.Run(() => reconciliationTrigger.TriggerImmediateReconciliationAsync(CancellationToken.None));

        return (200, new YouTubeCookiesResponse
        {
            Success = true,
            Message = "Cookies saved. Worker will attempt to recover any excluded YouTube sources within about a minute.",
        });
    }

    private async Task HandleHealthRequestAsync(HttpListenerContext context)
    {
        var snapshot = await healthSnapshotProvider.GetSnapshotAsync().ConfigureAwait(false);

        var response = new YouTubeHealthResponse
        {
            AuthAlertActive = snapshot.AuthAlertActive,
            CookiesFileExists = snapshot.CookiesValidation.FileExists,
            CookiesValid = snapshot.CookiesValidation.IsValid,
            CookieCount = snapshot.CookiesValidation.CookieCount,
            EarliestExpiration = snapshot.CookiesValidation.EarliestExpiration,
            HasYouTubeDomain = snapshot.CookiesValidation.HasYouTubeDomain,
            ExcludedSourceIds = snapshot.ExcludedTvSourceIds,
            TotalTvSources = snapshot.TotalTvSourceCount,
            ActiveTvSources = snapshot.ActiveTvSourceCount,
            Message = snapshot.CookiesValidation.Message ?? string.Empty,
        };

        await SendJsonResponse(context, 200, response).ConfigureAwait(false);
    }

    private async Task HandleCaptureStatusRequestAsync(HttpListenerContext context)
    {
        var dateParam = context.Request.QueryString["date"];
        var date = DateOnly.TryParseExact(dateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).DateTime);

        var response = await captureStatusSnapshotProvider.GetSnapshotAsync(date).ConfigureAwait(false);

        await SendJsonResponse(context, 200, response).ConfigureAwait(false);
    }

    private async Task HandleAudioSegmentRequestAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var sourceId = query["sourceId"];
        var format = query["format"] ?? "mp3";

        if (string.IsNullOrWhiteSpace(sourceId)
            || (format != "mp3" && format != "wav")
            || !DateTimeOffset.TryParse(query["startUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc)
            || !DateTimeOffset.TryParse(query["endUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var endUtc)
            || !int.TryParse(query["bitrateKbps"], out var bitrateKbps)
            || !int.TryParse(query["frequencyHz"], out var frequencyHz))
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse
            {
                Message = "Missing or invalid query parameters (sourceId, startUtc, endUtc, bitrateKbps, frequencyHz, format=mp3|wav).",
            }).ConfigureAwait(false);
            return;
        }

        var result = await closedHourAudioReader
            .ExtractSegmentAsync(sourceId, startUtc, endUtc, bitrateKbps, frequencyHz, format, CancellationToken.None)
            .ConfigureAwait(false);

        switch (result)
        {
            case ClosedHourAudioSuccess success:
                context.Response.StatusCode = 200;
                context.Response.ContentType = success.ContentType;
                context.Response.ContentLength64 = success.Bytes.Length;
                await context.Response.OutputStream.WriteAsync(success.Bytes).ConfigureAwait(false);
                context.Response.Close();
                break;

            case ClosedHourAudioStillRecording stillRecording:
                await SendJsonResponse(context, 409, new AudioSegmentErrorResponse
                {
                    Message = $"Source '{sourceId}' is still recording this hour; {stillRecording.RecordedSeconds:F0}s captured so far.",
                    RecordedSeconds = stillRecording.RecordedSeconds,
                }).ConfigureAwait(false);
                break;

            case ClosedHourAudioSourceNotFound:
                await SendJsonResponse(context, 404, new AudioSegmentErrorResponse
                {
                    Message = $"Unknown sourceId '{sourceId}'.",
                }).ConfigureAwait(false);
                break;

            case ClosedHourAudioNotAvailable notAvailable:
                await SendJsonResponse(context, 404, new AudioSegmentErrorResponse
                {
                    Message = notAvailable.Reason,
                }).ConfigureAwait(false);
                break;
        }
    }

    // Pure byte-for-byte proxy to the WhatsApp sidecar's own HTTP surface — no local caching or
    // storage on the worker side, matching how /youtube/health is always fetched live.
    //
    // Fetching from the sidecar and writing back to our own caller are two separate failure
    // domains, handled in two separate try blocks: if the sidecar fetch fails, nothing has been
    // sent to our caller yet, so it is safe to fall back to a 200 "unreachable" JSON response. If
    // writing that already-fetched body back to our caller fails instead (e.g. NestJS's own
    // AbortSignal.timeout fires and it tears down the connection mid-write — "network name is no
    // longer available"), headers/body are already partially sent, so HttpListenerResponse
    // refuses any further write; trying anyway throws InvalidOperationException, which used to
    // cascade into HandleRequest's outer catch trying (and failing) to write yet another error
    // response. There is nothing more to send once that happens — just log and stop.
    private async Task RelayToSidecarAsync(HttpListenerContext context, string sidecarPath)
    {
        byte[] body;
        HttpStatusCode statusCode;
        string? contentType;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts?.Token ?? CancellationToken.None);
            // Baileys does synchronous crypto/handshake work on Node's single event loop during
            // an actual login/QR-pairing event, which briefly (a few seconds) stalls every other
            // request the sidecar serves, including this one — not an outage, just momentarily
            // busy. Kept shorter than NestJS's own AbortSignal.timeout(6000) to the worker (see
            // whatsapp.service.ts) so there is still headroom to write the response back before
            // NestJS's clock runs out and aborts the connection out from under us.
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var response = await httpClient
                .GetAsync($"http://localhost:{whatsAppSidecarPort}{sidecarPath}", timeoutCts.Token)
                .ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            statusCode = response.StatusCode;
            contentType = response.Content.Headers.ContentType?.ToString();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[YouTubeCookiesHttpService] WhatsApp sidecar unreachable at {Path}.", sidecarPath);
            await SendJsonResponse(context, 200, new WhatsAppUnreachableResponse()).ConfigureAwait(false);
            return;
        }

        try
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = contentType ?? "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "[YouTubeCookiesHttpService] Caller disconnected while relaying {Path}.", sidecarPath);
        }
    }

    private sealed record WhatsAppUnreachableResponse(string Status = "worker_unreachable", string? Qr = null);

    private static string ResolveAbsolutePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    private static async Task SendJsonResponse<TResponse>(
        HttpListenerContext context,
        int statusCode,
        TResponse response)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);

        context.Response.Close();
    }
}
