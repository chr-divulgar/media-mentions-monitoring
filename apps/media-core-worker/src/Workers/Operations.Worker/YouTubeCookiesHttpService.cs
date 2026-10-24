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
///   GET  /video/segment    — cuts a window out of a TV channel's recorded MP4 (video/mp4), for the
///     alert editor's video flow; the clip's real start and length come back in headers.
///   POST /notes/pdf/preview — prints a web note's article page to a scratch PDF and returns the
///     public link it will have once saved ({outlet, title, url, date} → {previewId, link}).
///   GET  /notes/pdf/preview?id= — the previewed PDF itself, for the editor's viewer.
///   POST /notes/clip — files the clip of a radio or TV note in the archive account of its medium, under
///     {country}/{client}/{yyyy}/{MM}/{dd}/ (see NoteClipService);
///   POST /notes/pdf/commit  — on save, archives the previewed PDF over FTP ({previewId} → {link});
///     all three are consumed by NestJS's NotesService.
///   GET  /whatsapp/status  — relays the WhatsApp sidecar's own /status (connected/pending_qr/
///     disconnected), consumed by NestJS's WhatsAppService/web-ui's status tab.
///   GET  /whatsapp/qr      — relays the WhatsApp sidecar's own /qr (base64 PNG or null).
///   POST /whatsapp/send    — relays {number, message?, audioPath?, videoPath?} to the sidecar's /send.
///   GET  /whatsapp/groups  — relays the sidecar's /groups: the WhatsApp groups the linked number
///     belongs to, offered in the web-ui as alert recipients.
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
    private readonly VideoSegmentService videoSegmentService;
    // Null when the archive, its public URL or the headless browser is not configured.
    private readonly NotePdfService? notePdfService;
    private readonly NoteClipService? noteClipService;
    private readonly NoteArchiveRemover? noteArchiveRemover;
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
        VideoSegmentService videoSegmentService,
        HttpClient httpClient,
        NotePdfService? notePdfService = null,
        NoteClipService? noteClipService = null,
        NoteArchiveRemover? noteArchiveRemover = null)
    {
        this.noteArchiveRemover = noteArchiveRemover;
        this.notePdfService = notePdfService;
        this.noteClipService = noteClipService;
        this.logger = logger;
        this.options = options;
        this.cookiesValidator = cookiesValidator;
        this.alertService = alertService;
        this.healthSnapshotProvider = healthSnapshotProvider;
        this.reconciliationTrigger = reconciliationTrigger;
        this.captureStatusSnapshotProvider = captureStatusSnapshotProvider;
        this.closedHourAudioReader = closedHourAudioReader;
        this.videoSegmentService = videoSegmentService;
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

    // 64 = the network name is no longer available, 995 = the I/O was aborted: the caller hung up.
    private static bool IsClientGone(Exception exception) =>
        exception is HttpListenerException { ErrorCode: 64 or 995 } or IOException { InnerException: HttpListenerException { ErrorCode: 64 or 995 } };

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

            if (method == "GET" && path == "/video/segment")
            {
                await HandleVideoSegmentRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/notes/pdf/preview")
            {
                await HandleNotePdfPreviewRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/notes/archive/delete")
            {
                await HandleNoteArchiveDeleteRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/notes/pdf/upload")
            {
                await HandleNotePdfUploadRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/notes/pdf/preview")
            {
                await HandleNotePdfPreviewFileRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/notes/clip")
            {
                await HandleNoteClipRequestAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/notes/pdf/commit")
            {
                await HandleNotePdfCommitRequestAsync(context).ConfigureAwait(false);
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

            if (method == "POST" && path == "/whatsapp/send")
            {
                // Lets NestJS send a note (text + optional mp3 or mp4 clip) through the same Baileys
                // session as alerts, instead of the unpinned `npx mudslide` CLI. 90 s: uploading a
                // video clip takes far longer than a text or an mp3.
                using var bodyReader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var sendBody = await bodyReader.ReadToEndAsync().ConfigureAwait(false);
                await RelayToSidecarAsync(context, "/send", TimeSpan.FromSeconds(90), sendBody).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/whatsapp/groups")
            {
                // Listing every group the linked number is in can take longer than a status poll.
                await RelayToSidecarAsync(context, "/groups", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex) when (IsClientGone(ex))
        {
            logger.LogDebug("[YouTubeCookiesHttpService] The client closed the connection before {Path} was answered.", context.Request.Url?.AbsolutePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[YouTubeCookiesHttpService] Error handling {Method} {Path}", context.Request.HttpMethod, context.Request.Url?.AbsolutePath);
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

    // Windows longer than this are not an editor window (the longest is the 20-minute segment).
    private static readonly TimeSpan MaxVideoWindow = TimeSpan.FromHours(2);

    private async Task<bool> EnsureNotePdfConfiguredAsync(HttpListenerContext context)
    {
        if (notePdfService is not null)
        {
            return true;
        }

        await SendJsonResponse(context, 503, new AudioSegmentErrorResponse
        {
            Message = "La generación de PDF no está configurada (requiere ARCHIVE_FTP_*, ARCHIVE_PUBLIC_BASE_URL y el navegador headless).",
        }).ConfigureAwait(false);
        return false;
    }

    private async Task<T?> ReadJsonBodyAsync<T>(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private async Task HandleNoteClipRequestAsync(HttpListenerContext context)
    {
        if (noteClipService is null)
        {
            await SendJsonResponse(context, 503, new AudioSegmentErrorResponse { Message = "El archivo de notas no está disponible." }).ConfigureAwait(false);
            return;
        }

        var request = await ReadJsonBodyAsync<NoteClipRequest>(context).ConfigureAwait(false);
        if (request is null)
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse { Message = "Missing or invalid body." }).ConfigureAwait(false);
            return;
        }

        var result = await noteClipService.ArchiveAsync(request).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await SendJsonResponse(context, 502, new AudioSegmentErrorResponse { Message = result.Error ?? "No se pudo archivar el clip." }).ConfigureAwait(false);
            return;
        }

        await SendJsonResponse(context, 200, new NoteClipResponse { Path = result.Path!, Link = result.Link }).ConfigureAwait(false);
    }

    private async Task HandleNotePdfPreviewRequestAsync(HttpListenerContext context)
    {
        if (!await EnsureNotePdfConfiguredAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var request = await ReadJsonBodyAsync<NotePdfPreviewRequest>(context).ConfigureAwait(false);
        if (request is null
            || string.IsNullOrWhiteSpace(request.Title)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || request.Date == default)
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse
            {
                Message = "Missing or invalid body (title, an http(s) url and date are required).",
            }).ConfigureAwait(false);
            return;
        }

        var result = await notePdfService!.PreviewAsync(
            request.Outlet, request.Title, uri.ToString(), request.Date, request.Client, request.Country, request.Keywords, request.AlertId).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await SendJsonResponse(context, 502, new AudioSegmentErrorResponse { Message = result.Error ?? "No se pudo generar el PDF." }).ConfigureAwait(false);
            return;
        }

        await SendJsonResponse(context, 200, new NotePdfPreviewResponse { PreviewId = result.PreviewId!, Link = result.Link! }).ConfigureAwait(false);
    }

    // A deleted note's file in the archive: its links come in the body, what was deleted (and what could not be) goes back.
    private async Task HandleNoteArchiveDeleteRequestAsync(HttpListenerContext context)
    {
        if (noteArchiveRemover is null)
        {
            await SendJsonResponse(context, 503, new AudioSegmentErrorResponse { Message = "El archivo de notas no está configurado." }).ConfigureAwait(false);
            return;
        }

        var request = await ReadJsonBodyAsync<NoteArchiveDeleteRequest>(context).ConfigureAwait(false);
        var result = await noteArchiveRemover.DeleteAsync(request?.Links ?? []).ConfigureAwait(false);
        await SendJsonResponse(context, 200, new { deleted = result.Deleted, failed = result.Failed }).ConfigureAwait(false);
    }

    // A PDF the user made by hand: the bytes are the body, what the note is about is in the query.
    private async Task HandleNotePdfUploadRequestAsync(HttpListenerContext context)
    {
        if (!await EnsureNotePdfConfiguredAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var query = context.Request.QueryString;
        if (string.IsNullOrWhiteSpace(query["title"])
            || !DateTimeOffset.TryParse(query["date"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse { Message = "Missing or invalid query parameters (title and date are required)." }).ConfigureAwait(false);
            return;
        }

        // One byte over the limit is enough to know it is too big: never read an unbounded body.
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while (body.Length <= NotePdfService.MaxUploadBytes
               && (read = await context.Request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            body.Write(buffer, 0, read);
        }

        var result = await notePdfService!.PreviewFromFileAsync(
            body.ToArray(), query["outlet"] ?? string.Empty, query["title"]!, date, query["client"], query["country"]).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await SendJsonResponse(context, 502, new AudioSegmentErrorResponse { Message = result.Error ?? "No se pudo guardar el PDF." }).ConfigureAwait(false);
            return;
        }

        await SendJsonResponse(context, 200, new NotePdfPreviewResponse { PreviewId = result.PreviewId!, Link = result.Link! }).ConfigureAwait(false);
    }

    private async Task HandleNotePdfPreviewFileRequestAsync(HttpListenerContext context)
    {
        if (!await EnsureNotePdfConfiguredAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var path = notePdfService!.FindPreviewFile(context.Request.QueryString["id"] ?? string.Empty);
        if (path is null)
        {
            await SendJsonResponse(context, 404, new AudioSegmentErrorResponse
            {
                Message = "La vista previa del PDF expiró. Cierra y vuelve a abrir la nota.",
            }).ConfigureAwait(false);
            return;
        }

        // Streamed from the scratch file, never buffered.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/pdf";
        context.Response.ContentLength64 = file.Length;
        await file.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleNotePdfCommitRequestAsync(HttpListenerContext context)
    {
        if (!await EnsureNotePdfConfiguredAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var request = await ReadJsonBodyAsync<NotePdfCommitRequest>(context).ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.PreviewId))
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse { Message = "Missing previewId." }).ConfigureAwait(false);
            return;
        }

        var result = await notePdfService!.CommitAsync(request.PreviewId).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await SendJsonResponse(context, 502, new AudioSegmentErrorResponse { Message = result.Error ?? "No se pudo guardar el PDF." }).ConfigureAwait(false);
            return;
        }

        await SendJsonResponse(context, 200, new NotePdfResponse { Link = result.Link! }).ConfigureAwait(false);
    }

    private async Task HandleVideoSegmentRequestAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var sourceId = query["sourceId"];

        if (string.IsNullOrWhiteSpace(sourceId)
            || !DateTimeOffset.TryParse(query["startUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc)
            || !DateTimeOffset.TryParse(query["endUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var endUtc)
            || endUtc <= startUtc
            || endUtc - startUtc > MaxVideoWindow)
        {
            await SendJsonResponse(context, 400, new AudioSegmentErrorResponse
            {
                Message = "Missing or invalid query parameters (sourceId, startUtc, endUtc; endUtc after startUtc, at most 2 hours).",
            }).ConfigureAwait(false);
            return;
        }

        // exact=1: the clip a note keeps, which must start and end on the instants asked; without it a plain copy.
        var exact = query["exact"] is "1" or "true";
        var result = await videoSegmentService
            .GetSegmentAsync(sourceId, startUtc, endUtc, CancellationToken.None, exact)
            .ConfigureAwait(false);

        if (result is VideoSegmentNotAvailable notAvailable)
        {
            await SendJsonResponse(context, 404, new AudioSegmentErrorResponse { Message = notAvailable.Reason }).ConfigureAwait(false);
            return;
        }

        var clip = (VideoSegmentSuccess)result;
        try
        {
            // Streamed from the temp file, never buffered: a clip is tens of MB.
            await using var file = new FileStream(clip.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength64 = file.Length;
            context.Response.Headers["X-Clip-Start-Utc"] = clip.ClipStartUtc.ToString("o", CultureInfo.InvariantCulture);
            context.Response.Headers["X-Clip-Duration-Seconds"] = clip.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture);
            await file.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The caller gave up mid-transfer (its own timeout): headers and part of the body are
            // already out, so there is nothing more to send — only the temp file to remove.
            logger.LogDebug(exception, "[YouTubeCookiesHttpService] Caller disconnected while receiving a video clip.");
        }
        finally
        {
            try
            {
                File.Delete(clip.TempPath);
            }
            catch (IOException)
            {
                // Left behind in the temp folder.
            }
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
    private async Task RelayToSidecarAsync(
        HttpListenerContext context, string sidecarPath, TimeSpan? timeout = null, string? postBody = null)
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
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));

            var sidecarUrl = $"http://localhost:{whatsAppSidecarPort}{sidecarPath}";
            using var response = postBody is null
                ? await httpClient.GetAsync(sidecarUrl, timeoutCts.Token).ConfigureAwait(false)
                : await httpClient
                    .PostAsync(sidecarUrl, new StringContent(postBody, Encoding.UTF8, "application/json"), timeoutCts.Token)
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
