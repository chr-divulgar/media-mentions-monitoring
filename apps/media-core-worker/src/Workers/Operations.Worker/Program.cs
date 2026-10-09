using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Infrastructure;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Segmentation.Application;
using MediaOpsCore.Workers.Operations;

// Windows Services start with C:\Windows\System32 as their working directory — every
// relative path this worker resolves (stage/, .env, capture-sources.json, ...) assumes it is
// the exe's own directory instead, same as running via `dotnet run` or a double-click. Set it
// before anything below reads a relative path.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

DotEnvLoader.LoadIfPresent();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "MediaMentionsMonitoringWorker");

var options = OperationsWorkerOptionsLoader.Load();

builder.Services.AddSingleton(options);

// Add hosted service for YouTube cookies HTTP endpoint
builder.Services.AddSingleton<VideoSegmentService>();
// Internet access comes first: registered before every other hosted service so it is the first one
// started, and Program waits on it (below) before touching any source. See ConnectivityMonitor.
builder.Services.AddSingleton<ConnectivityMonitor>();
builder.Services.AddSingleton<IConnectivityMonitor>(sp => sp.GetRequiredService<ConnectivityMonitor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectivityMonitor>());
builder.Services.AddSingleton<YouTubeCookiesHttpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YouTubeCookiesHttpService>());
// Archive of closed TV video: upload to the server, and only then let the purge delete it. The
// ledger exists whenever archiving is switched on — even while the credentials are still missing —
// so the purge is already in archive mode and, with nothing verified, never deletes an .mp4.
if (options.ArchiveEnabled)
{
	builder.Services.AddSingleton(new ArchiveLedger(
		Path.Combine(options.StageFilesystemRootPath, "monitoringArtifacts", "archive-ledger.jsonl")));
}
if (options.IsArchiveActive)
{
	builder.Services.AddSingleton<IArchiveStorage, FtpArchiveStorage>();
	builder.Services.AddHostedService<ArchiveUploadWorker>();
}
// One headless Chromium shared by the anti-bot fallback of HtmlArticleReader and the note PDFs.
if (options.HeadlessBrowserEnabled)
{
	builder.Services.AddSingleton(sp => new HeadlessBrowser(
		options.HeadlessBrowserPath,
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HeadlessBrowser>>()));
}
// PDF copy of a web note (RQ-014): needs the archive, its public https base URL and the browser.
if (options.IsArchiveActive && options.ArchivePublicBaseUrl is not null && options.HeadlessBrowserEnabled)
{
	builder.Services.AddSingleton(sp => new NotePdfService(
		sp.GetRequiredService<HeadlessBrowser>(),
		sp.GetRequiredService<IArchiveStorage>(),
		options.ArchivePublicBaseUrl,
		Path.Combine(options.StageFilesystemRootPath, "note-pdf"),
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotePdfService>>()));
}
builder.Services.AddHostedService(sp => new RetentionPurgeWorker(
	options,
	sp.GetRequiredService<ISourceIncidentRecorder>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RetentionPurgeWorker>>(),
	archiveLedger: sp.GetService<ArchiveLedger>()));
builder.Services.AddSingleton<InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<StageMirrorMonitoringArtifactRepository>();
builder.Services.AddSingleton<IMonitoringArtifactRepository>(
    sp => sp.GetRequiredService<StageMirrorMonitoringArtifactRepository>());
builder.Services.AddSingleton<IEvidenceFileStore, FileSystemEvidenceStore>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
// Why a source has gaps: failures, retries and recoveries, per source and hour. The status page
// reads it; the capture and recovery paths write it.
builder.Services.AddSingleton(new SourceIncidentLog(Path.Combine(options.StageFilesystemRootPath, "monitoringArtifacts")));
builder.Services.AddSingleton<ISourceIncidentRecorder>(sp => sp.GetRequiredService<SourceIncidentLog>());
builder.Services.AddSingleton<ISourceIncidentReader>(sp => sp.GetRequiredService<SourceIncidentLog>());
// The configuration lives in the local MongoDB (`config` database): the capture sources
// (`platforms`), the clients and the backup copy of the users. apps/web-api writes it through its
// settings screens; there is no file or Firestore catalogue any more.
builder.Services.AddSingleton<MongoConfigStore>();
builder.Services.AddSingleton<ICaptureSourceRepository, MongoCaptureSourceRepository>();
// Capture coverage audit trail: one document per clock hour.
builder.Services.AddSingleton<ICaptureSummaryStore, MongoCaptureSummaryStore>();
builder.Services.AddHostedService<CaptureSummaryArchiveWorker>();
builder.Services.AddSingleton<StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ICaptureSourceProvider>(sp => sp.GetRequiredService<StaticCaptureSourceProvider>());
builder.Services.AddSingleton<IStartupStreamValidator, FfmpegStartupStreamValidator>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IStartupSourceDiscoveryService, HttpStartupSourceDiscoveryService>();
builder.Services.AddSingleton<IProcessRunner, LocalSystemProcessRunner>();
builder.Services.AddSingleton<YtdlpBinaryProvider>();
builder.Services.AddSingleton<IYtdlpBinaryProvider>(sp => sp.GetRequiredService<YtdlpBinaryProvider>());
// Users who opted in (Settings > Usuarios) get a WhatsApp when the platform itself needs a human.
builder.Services.AddSingleton<IPlatformErrorNotifier>(sp => new PlatformErrorNotifier(
	sp.GetRequiredService<MongoConfigStore>(),
	sp.GetRequiredService<HttpWhatsAppAlertNotifier>(),
	options.Smtp is null ? null : sp.GetRequiredService<SmtpAlertEmailNotifier>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlatformErrorNotifier>>()));
builder.Services.AddHostedService(sp => new WhatsAppConnectionWatchdog(
	options,
	sp.GetRequiredService<HttpClient>(),
	sp.GetRequiredService<IPlatformErrorNotifier>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WhatsAppConnectionWatchdog>>()));
builder.Services.AddSingleton<IYouTubeCookiesAlertService, YouTubeCookiesAlertService>();
builder.Services.AddSingleton<IYouTubeCookiesValidator, YouTubeCookiesValidator>();
builder.Services.AddSingleton<IYouTubeHealthSnapshotProvider, YouTubeHealthSnapshotProvider>();
builder.Services.AddSingleton<ICaptureStatusSnapshotProvider>(sp => new CaptureStatusSnapshotProvider(
	sp.GetRequiredService<ICaptureSourceRepository>(),
	sp.GetRequiredService<IEvidenceFileStore>(),
	sp.GetRequiredService<StageMirrorMonitoringArtifactRepository>(),
	sp.GetRequiredService<ILiveCaptureProgressReader>(),
	sp.GetService<ISourceIncidentReader>(),
	options.WebNewsEnabled));
builder.Services.AddSingleton<IYouTubeLiveProbe>(sp => new YouTubeLiveProbe(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<YtdlpLiveStreamUrlResolver>();
builder.Services.AddSingleton<ILiveStreamUrlResolver>(sp => sp.GetRequiredService<YtdlpLiveStreamUrlResolver>());
if (options.TvVideoEnabled)
{
	builder.Services.AddHostedService<TvVideoRecorderWorker>();
}
builder.Services.AddSingleton<IStartupSourceInitializationService, StartupSourceInitializationService>();
builder.Services.AddSingleton<SourceAvailabilityReconciliationService>();
builder.Services.AddSingleton<ICaptureAttemptObserver>(sp => sp.GetRequiredService<SourceAvailabilityReconciliationService>());
builder.Services.AddSingleton<IYouTubeReconciliationTrigger>(sp => sp.GetRequiredService<SourceAvailabilityReconciliationService>());
builder.Services.AddSingleton<IPluginProfileProvider, JsonPluginProfileProvider>();
builder.Services.AddSingleton<IIngestionPluginResolver, MediaPlatformIngestionPluginResolver>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
builder.Services.AddSingleton(new IncrementalSegmentationOptions
{
	SegmentDurationSeconds = options.SegmentDurationSeconds
});
builder.Services.AddSingleton(new MongoAlertingOptions
{
	ConnectionString = options.MongoConnectionString,
	ConfigDatabaseName = options.MongoConfigDatabaseName,
	MonitoringDatabaseName = options.MongoMonitoringDatabaseName,
	AlertCollectionName = options.MongoAlertCollectionName
});
// Client/keyword config (for alert detection): the `config.clients` collection apps/web-api manages.
builder.Services.AddSingleton<IClientConfigRepository, MongoClientConfigRepository>();
builder.Services.AddSingleton<MongoAlertRepository>();
builder.Services.AddSingleton<IAlertRepository>(sp => sp.GetRequiredService<MongoAlertRepository>());
builder.Services.AddSingleton<IDailyHeadlinesSource>(sp => sp.GetRequiredService<MongoAlertRepository>());
builder.Services.AddSingleton(new WhatsAppSidecarOptions
{
	BaseUrl = $"http://localhost:{options.WhatsAppSidecarPort}"
});
builder.Services.AddSingleton<HttpWhatsAppAlertNotifier>();
if (options.Smtp is not null)
{
	builder.Services.AddSingleton(options.Smtp);
	builder.Services.AddSingleton<SmtpAlertEmailNotifier>();
}
// Email recipients go over SMTP when it is configured and are skipped otherwise; every other
// recipient (phone number, WhatsApp group id) goes to the WhatsApp sidecar.
builder.Services.AddSingleton<IAlertNotifier>(sp => new RoutingAlertNotifier(
	sp.GetRequiredService<HttpWhatsAppAlertNotifier>(),
	options.Smtp is null ? null : sp.GetRequiredService<SmtpAlertEmailNotifier>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RoutingAlertNotifier>>()));
// Daily headlines digest (RQ-035) — off unless DailyHeadlinesEnabled.
if (options.DailyHeadlinesEnabled)
{
	builder.Services.AddSingleton<IMessageNotifier>(sp => new RoutingMessageNotifier(
		sp.GetRequiredService<HttpWhatsAppAlertNotifier>(),
		options.Smtp is null ? null : sp.GetRequiredService<SmtpAlertEmailNotifier>(),
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RoutingMessageNotifier>>()));
	builder.Services.AddSingleton<IDailyDigestLog>(sp => new MongoDailyDigestLog(sp.GetRequiredService<MongoAlertingOptions>()));
	builder.Services.AddSingleton<IDailyHeadlinesDigestUseCase, DailyHeadlinesDigestUseCase>();
	builder.Services.AddHostedService(sp => new DailyHeadlinesWorker(
		sp.GetRequiredService<IDailyHeadlinesDigestUseCase>(),
		sp.GetRequiredService<IDailyDigestLog>(),
		TimeOnly.ParseExact(options.DailyHeadlinesTime, "HH:mm"),
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DailyHeadlinesWorker>>()));
}
builder.Services.AddSingleton<WhatsAppSidecarProcessSupervisor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WhatsAppSidecarProcessSupervisor>());
builder.Services.AddSingleton<IDetectAlertsUseCase, DetectAlertsUseCase>();
builder.Services.AddHostedService<PendingAlertNotificationRetryWorker>();
// IAudioCapturePlugin gets observer and repository so sessions report events directly.
builder.Services.AddSingleton<IAudioCapturePlugin>(sp => new InProcessFfmpegAudioCapturePlugin(
	sp.GetRequiredService<OperationsWorkerOptions>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InProcessFfmpegAudioCapturePlugin>>(),
	sp.GetRequiredService<IOperationalMetrics>(),
	sp.GetRequiredService<ICaptureAttemptObserver>(),
	sp.GetRequiredService<IMonitoringArtifactRepository>(),
	sp.GetRequiredService<IDetectAlertsUseCase>()));
builder.Services.AddSingleton<ILiveCaptureProgressReader>(
	sp => (ILiveCaptureProgressReader)sp.GetRequiredService<IAudioCapturePlugin>());
builder.Services.AddSingleton<IClosedHourAudioReader, ClosedHourAudioSegmentReader>();
builder.Services.AddSingleton<IContinuousCaptureUseCase>(sp => new ContinuousCaptureUseCase(
	sp.GetRequiredService<ICaptureSourceProvider>(),
	sp.GetRequiredService<IIngestionPluginResolver>(),
	sp.GetRequiredService<IAudioCapturePlugin>(),
	options.CaptureMaxDegreeOfParallelism));
builder.Services.AddSingleton<IIncrementalSegmentationUseCase, IncrementalSegmentationUseCase>();
// First discrete-ingestion plugin: Google News search -> outlet page -> WhatsApp alert. Off by
// default (see OperationsWorkerOptions.WebNewsEnabled); when disabled, DiscreteIngestionOrchestrator
// gets no IWebNewsCaptureUseCase (GetService, not GetRequiredService) and stays a no-op.
if (options.WebNewsEnabled)
{
	builder.Services.AddSingleton<INewsSearchProvider>(sp => new GoogleNewsRssSearchProvider(
		sp.GetRequiredService<HttpClient>(),
		options.WebNewsRequestTimeoutSeconds,
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GoogleNewsRssSearchProvider>>()));
	builder.Services.AddSingleton<INewsLinkResolver>(sp => new GoogleNewsLinkResolver(
		sp.GetRequiredService<HttpClient>(),
		options.WebNewsRequestTimeoutSeconds,
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GoogleNewsLinkResolver>>()));
	builder.Services.AddSingleton<IArticleReader>(sp => new HtmlArticleReader(
		sp.GetRequiredService<HttpClient>(),
		options.WebNewsRequestTimeoutSeconds,
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HtmlArticleReader>>(),
		sp.GetService<HeadlessBrowser>()));
	builder.Services.AddSingleton<IWebArticleRepository>(sp => new MongoWebArticleRepository(
		sp.GetRequiredService<MongoAlertingOptions>(),
		options.WebNewsMaxFailedAttempts));
	builder.Services.AddSingleton<IWebNewsCaptureUseCase>(sp => new WebNewsCaptureUseCase(
		sp.GetRequiredService<IClientConfigRepository>(),
		sp.GetRequiredService<IDetectAlertsUseCase>(),
		sp.GetRequiredService<INewsSearchProvider>(),
		sp.GetRequiredService<INewsLinkResolver>(),
		sp.GetRequiredService<IArticleReader>(),
		sp.GetRequiredService<IWebArticleRepository>(),
		sp.GetRequiredService<ISourceIncidentRecorder>(),
		options.WebNewsKeywordsPerQuery,
		options.WebNewsMaxConcurrency));
}
builder.Services.AddSingleton<IDiscreteIngestionOrchestrator>(sp => new DiscreteIngestionOrchestrator(
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiscreteIngestionOrchestrator>>(),
	sp.GetService<IWebNewsCaptureUseCase>()));
builder.Services.AddHostedService<IncrementalSegmentationWorker>();
builder.Services.AddHostedService<DiscreteIngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceAvailabilityReconciliationService>());

var host = builder.Build();

// Lets the status page tell "the worker restarted" apart from "the stream failed" on a gray stretch.
var incidentRecorder = host.Services.GetRequiredService<ISourceIncidentRecorder>();
incidentRecorder.Record(IncidentKinds.WorkerSourceId, IncidentKinds.WorkerStarted);
if (options.ArchiveEnabled && !options.IsArchiveActive)
{
    // Archiving is on but cannot upload: TV video piles up locally (the purge keeps every .mp4
    // until its copy is verified). Say so on /status instead of failing silently.
    incidentRecorder.Record(
        IncidentKinds.WorkerSourceId, IncidentKinds.ArchiveFailed,
        "Faltan ARCHIVE_FTP_URL / ARCHIVE_FTP_USER / ARCHIVE_FTP_PASSWORD: el video MP4 no se archiva ni se borra.");
}
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    () => incidentRecorder.Record(IncidentKinds.WorkerSourceId, IncidentKinds.WorkerStopped));

// host.StartAsync() runs every registered IHostedService.StartAsync() (in registration order —
// YouTubeCookiesHttpService and WhatsAppSidecarProcessSupervisor among them, so NestJS can reach
// the worker and the sidecar is already up before anything relays to it) and, since
// AddWindowsService() is registered above, is also what tells the Service Control Manager this
// service is now running. Calling it here — before the manual startup steps below, which can take
// minutes with many sources — is what keeps `sc start` from hitting its ~30s default timeout
// (error 1053) waiting for a status update that used to only arrive at host.RunAsync(), at the
// very end of this file.
await host.StartAsync();

// First thing the worker does: make sure there is internet. Without it the source catalogue would
// come from the stale local copy and every source would fail validation and be marked excluded, so
// the worker waits here (the monitor retries every minute) and carries on once the connection is up.
// After StartAsync on purpose: that is what tells the Service Control Manager the service is running.
await host.Services.GetRequiredService<IConnectivityMonitor>().WaitUntilOnlineAsync();

// The configuration is in the local MongoDB: it may still be starting when the machine boots.
await host.Services.GetRequiredService<MongoConfigStore>()
	.WaitUntilReadyAsync(host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MongoConfigStore>>());

// Pre-warm yt-dlp binary resolution so it is ready before the first TV source capture.
// Logs a warning and continues if yt-dlp cannot be found or downloaded.
try
{
    await host.Services.GetRequiredService<YtdlpBinaryProvider>().InitializeAsync();
}
catch (Exception ex)
{
    var log = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<YtdlpBinaryProvider>>();
    log.LogWarning(ex, "[YtdlpBinaryProvider] Pre-warmup failed. TV sources will be excluded at startup.");
}

await host.Services.GetRequiredService<IStartupSourceInitializationService>().InitializeAsync();

// Resend any alert notification left incomplete by a previous restart or WhatsApp outage. Placed
// after source validation above (which takes minutes) so the WhatsApp sidecar, started earlier in
// this file, has had time to connect/pair first.
try
{
    await host.Services.GetRequiredService<IDetectAlertsUseCase>().RetryPendingNotificationsAsync();
}
catch (Exception ex)
{
    var log = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    log.LogWarning(ex, "[Program] Retrying pending alert notifications failed at startup.");
}

// Start capture sessions once for all initially resolved sources.
// After this point sessions are self-sustaining: failures trigger hot recovery,
// recoveries call TriggerCaptureAsync — no periodic heartbeat required.
await host.Services.GetRequiredService<IContinuousCaptureUseCase>().ExecuteAsync();
await host.WaitForShutdownAsync();
