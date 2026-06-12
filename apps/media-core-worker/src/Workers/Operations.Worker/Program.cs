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
builder.Services.AddSingleton<StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ICaptureSourceProvider>(sp => sp.GetRequiredService<StaticCaptureSourceProvider>());
builder.Services.AddSingleton<IStartupStreamValidator, FfmpegStartupStreamValidator>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IStartupSourceDiscoveryService, HttpStartupSourceDiscoveryService>();
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
await host.Services.GetRequiredService<IStartupSourceInitializationService>().InitializeAsync();
// Start capture sessions once for all initially resolved sources.
// After this point sessions are self-sustaining: failures trigger hot recovery,
// recoveries call TriggerCaptureAsync — no periodic heartbeat required.
await host.Services.GetRequiredService<IContinuousCaptureUseCase>().ExecuteAsync();
await host.RunAsync();
