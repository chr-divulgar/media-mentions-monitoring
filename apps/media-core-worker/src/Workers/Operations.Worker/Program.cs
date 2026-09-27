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
builder.Services.AddSingleton<YouTubeCookiesHttpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YouTubeCookiesHttpService>());
builder.Services.AddSingleton<InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<StageMirrorMonitoringArtifactRepository>();
builder.Services.AddSingleton<IMonitoringArtifactRepository>(
    sp => sp.GetRequiredService<StageMirrorMonitoringArtifactRepository>());
builder.Services.AddSingleton<IEvidenceFileStore, FileSystemEvidenceStore>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
// Capture source repositories: Firestore primary (when configured, same "platforms" collection
// and service account apps/web-api already uses) + JSON file fallback.
builder.Services.AddSingleton<JsonFileCaptureSourceRepository>();
if (options.Firestore?.IsEnabled == true)
{
    var firestoreOptions = options.Firestore;
    builder.Services.AddSingleton(firestoreOptions);
    builder.Services.AddSingleton(sp => new GoogleServiceAccountTokenProvider(
        sp.GetRequiredService<HttpClient>(),
        firestoreOptions.ClientEmail!,
        firestoreOptions.PrivateKeyPem!));
    builder.Services.AddSingleton<FirestoreCaptureSourceRepository>();
    // Capture coverage audit trail: one document per clock hour in the same Firestore project.
    builder.Services.AddSingleton<FirestoreCaptureSummaryStore>();
    builder.Services.AddHostedService<CaptureSummaryArchiveWorker>();
    builder.Services.AddSingleton<ICaptureSourceRepository>(sp =>
        new FallbackCaptureSourceRepository(
            sp.GetRequiredService<FirestoreCaptureSourceRepository>(),
            sp.GetRequiredService<JsonFileCaptureSourceRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackCaptureSourceRepository>>()));
}
else
{
    builder.Services.AddSingleton<ICaptureSourceRepository>(sp =>
        sp.GetRequiredService<JsonFileCaptureSourceRepository>());
}
builder.Services.AddSingleton<StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ICaptureSourceProvider>(sp => sp.GetRequiredService<StaticCaptureSourceProvider>());
builder.Services.AddSingleton<IStartupStreamValidator, FfmpegStartupStreamValidator>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IStartupSourceDiscoveryService, HttpStartupSourceDiscoveryService>();
builder.Services.AddSingleton<IProcessRunner, LocalSystemProcessRunner>();
builder.Services.AddSingleton<YtdlpBinaryProvider>();
builder.Services.AddSingleton<IYtdlpBinaryProvider>(sp => sp.GetRequiredService<YtdlpBinaryProvider>());
builder.Services.AddSingleton<IYouTubeCookiesAlertService, YouTubeCookiesAlertService>();
builder.Services.AddSingleton<IYouTubeCookiesValidator, YouTubeCookiesValidator>();
builder.Services.AddSingleton<IYouTubeHealthSnapshotProvider, YouTubeHealthSnapshotProvider>();
builder.Services.AddSingleton<ICaptureStatusSnapshotProvider, CaptureStatusSnapshotProvider>();
builder.Services.AddSingleton<ILiveStreamUrlResolver, YtdlpLiveStreamUrlResolver>();
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
// Client/keyword config (for alert detection) comes from the same Firestore "clients" collection
// apps/web-api already manages — the config operators actually edit today. No local Mongo
// fallback: see NullClientConfigRepository's own doc comment for why.
if (options.Firestore?.IsEnabled == true)
{
	var clientConfigFirestoreOptions = new FirestoreCaptureSourceRepositoryOptions
	{
		ProjectId = options.Firestore.ProjectId,
		ClientEmail = options.Firestore.ClientEmail,
		PrivateKeyPem = options.Firestore.PrivateKeyPem,
		CollectionPath = "clients",
		RequestTimeoutSeconds = options.Firestore.RequestTimeoutSeconds
	};
	builder.Services.AddSingleton<IClientConfigRepository>(sp => new FirestoreClientConfigRepository(
		sp.GetRequiredService<HttpClient>(),
		sp.GetRequiredService<GoogleServiceAccountTokenProvider>(),
		clientConfigFirestoreOptions,
		sp.GetRequiredService<ICaptureSourceProvider>(),
		sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FirestoreClientConfigRepository>>()));
}
else
{
	builder.Services.AddSingleton<IClientConfigRepository, NullClientConfigRepository>();
}
builder.Services.AddSingleton<IAlertRepository, MongoAlertRepository>();
builder.Services.AddSingleton(new WhatsAppSidecarOptions
{
	BaseUrl = $"http://localhost:{options.WhatsAppSidecarPort}"
});
builder.Services.AddSingleton<IAlertNotifier, HttpWhatsAppAlertNotifier>();
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
builder.Services.AddSingleton<IDiscreteIngestionOrchestrator, DiscreteIngestionOrchestrator>();
builder.Services.AddHostedService<IncrementalSegmentationWorker>();
builder.Services.AddHostedService<DiscreteIngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceAvailabilityReconciliationService>());

var host = builder.Build();

// Hosted services only start at host.RunAsync(), after startup source validation below — which
// can take minutes with many sources. Start these two now so NestJS can reach the worker (status,
// health) and the WhatsApp sidecar is already up by the time anything relays to it, instead of
// both reporting "unreachable" for that whole window.
await host.Services.GetRequiredService<YouTubeCookiesHttpService>().StartAsync(CancellationToken.None);
await host.Services.GetRequiredService<WhatsAppSidecarProcessSupervisor>().StartAsync(CancellationToken.None);

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
await host.RunAsync();
