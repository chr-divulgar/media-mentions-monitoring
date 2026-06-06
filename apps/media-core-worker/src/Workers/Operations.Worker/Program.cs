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
builder.Services.AddSingleton<IAudioCapturePlugin, InProcessFfmpegAudioCapturePlugin>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
builder.Services.AddSingleton<StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ICaptureSourceProvider>(sp => sp.GetRequiredService<StaticCaptureSourceProvider>());
builder.Services.AddSingleton<IStartupStreamValidator, FfmpegStartupStreamValidator>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IStartupSourceDiscoveryService, HttpStartupSourceDiscoveryService>();
builder.Services.AddSingleton<IStartupSourceInitializationService, StartupSourceInitializationService>();
builder.Services.AddSingleton<IPluginProfileProvider, JsonPluginProfileProvider>();
builder.Services.AddSingleton<IIngestionPluginResolver, MediaPlatformIngestionPluginResolver>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
builder.Services.AddSingleton(new IncrementalSegmentationOptions
{
SegmentDurationSeconds = options.SegmentDurationSeconds
});
builder.Services.AddSingleton<IContinuousCaptureUseCase>(sp => new ContinuousCaptureUseCase(
	sp.GetRequiredService<ICaptureSourceProvider>(),
	sp.GetRequiredService<IIngestionPluginResolver>(),
	sp.GetRequiredService<IAudioCapturePlugin>(),
	sp.GetRequiredService<IMonitoringArtifactRepository>(),
	options.CaptureMaxDegreeOfParallelism));
builder.Services.AddSingleton<IIncrementalSegmentationUseCase, IncrementalSegmentationUseCase>();
builder.Services.AddSingleton<IContinuousIngestionOrchestrator, ContinuousIngestionOrchestrator>();
builder.Services.AddSingleton<IDiscreteIngestionOrchestrator, DiscreteIngestionOrchestrator>();
builder.Services.AddHostedService<ContinuousIngestionWorker>();
builder.Services.AddHostedService<DiscreteIngestionWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<IStartupSourceInitializationService>().InitializeAsync();
await host.RunAsync();
