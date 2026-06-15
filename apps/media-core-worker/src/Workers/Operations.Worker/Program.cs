using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Segmentation.Application;
using MediaOpsCore.Workers.Operations;

var builder = Host.CreateApplicationBuilder(args);

var options = OperationsWorkerOptionsLoader.Load();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<IMonitoringArtifactRepository, StageMirrorMonitoringArtifactRepository>();
builder.Services.AddSingleton<IEvidenceFileStore, FileSystemEvidenceStore>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
// Capture source repositories: Firebase primary (when configured) + JSON file fallback.
builder.Services.AddSingleton<JsonFileCaptureSourceRepository>();
if (options.FirebaseDatabase?.IsEnabled == true)
{
    builder.Services.AddSingleton(options.FirebaseDatabase);
    builder.Services.AddSingleton<FirebaseCaptureSourceRepository>();
    builder.Services.AddSingleton<ICaptureSourceRepository>(sp =>
        new FallbackCaptureSourceRepository(
            sp.GetRequiredService<FirebaseCaptureSourceRepository>(),
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
builder.Services.AddSingleton<ILiveStreamUrlResolver, YtdlpLiveStreamUrlResolver>();
builder.Services.AddSingleton<IStartupSourceInitializationService, StartupSourceInitializationService>();
builder.Services.AddSingleton<SourceAvailabilityReconciliationService>();
builder.Services.AddSingleton<ICaptureAttemptObserver>(sp => sp.GetRequiredService<SourceAvailabilityReconciliationService>());
builder.Services.AddSingleton<IPluginProfileProvider, JsonPluginProfileProvider>();
builder.Services.AddSingleton<IIngestionPluginResolver, MediaPlatformIngestionPluginResolver>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
builder.Services.AddSingleton(new IncrementalSegmentationOptions
{
	SegmentDurationSeconds = options.SegmentDurationSeconds
});
// IAudioCapturePlugin gets observer and repository so sessions report events directly.
builder.Services.AddSingleton<IAudioCapturePlugin>(sp => new InProcessFfmpegAudioCapturePlugin(
	sp.GetRequiredService<OperationsWorkerOptions>(),
	sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InProcessFfmpegAudioCapturePlugin>>(),
	sp.GetRequiredService<IOperationalMetrics>(),
	sp.GetRequiredService<ICaptureAttemptObserver>(),
	sp.GetRequiredService<IMonitoringArtifactRepository>()));
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
// Start capture sessions once for all initially resolved sources.
// After this point sessions are self-sustaining: failures trigger hot recovery,
// recoveries call TriggerCaptureAsync — no periodic heartbeat required.
await host.Services.GetRequiredService<IContinuousCaptureUseCase>().ExecuteAsync();
await host.RunAsync();
