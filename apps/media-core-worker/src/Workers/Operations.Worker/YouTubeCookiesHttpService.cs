using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Hosted service exposing two routes for the YouTube cookies workflow, on http://localhost:5000:
///   POST /youtube/cookies — receives fresh cookies from NestJS, validates them, writes them to
///     disk, clears the auth alert, and triggers an immediate reconciliation attempt.
///   GET  /youtube/health   — reports auth alert / cookie validity / excluded-source state.
///     The HTTP 200 response itself is the liveness signal callers are checking for.
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
    private HttpListener? httpListener;
    private CancellationTokenSource? cts;

    public YouTubeCookiesHttpService(
        ILogger<YouTubeCookiesHttpService> logger,
        OperationsWorkerOptions options,
        IYouTubeCookiesValidator cookiesValidator,
        IYouTubeCookiesAlertService alertService,
        IYouTubeHealthSnapshotProvider healthSnapshotProvider,
        IYouTubeReconciliationTrigger reconciliationTrigger)
    {
        this.logger = logger;
        this.options = options;
        this.cookiesValidator = cookiesValidator;
        this.alertService = alertService;
        this.healthSnapshotProvider = healthSnapshotProvider;
        this.reconciliationTrigger = reconciliationTrigger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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
