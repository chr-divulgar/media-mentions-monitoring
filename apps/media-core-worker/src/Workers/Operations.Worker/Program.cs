using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.ProcessGuardian.Application;
using MediaOpsCore.Modules.Segmentation.Application;
using MediaOpsCore.Workers.Operations;

var builder = Host.CreateApplicationBuilder(args);

var options = new OperationsWorkerOptions();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<IMonitoringArtifactRepository, StageMirrorMonitoringArtifactRepository>();
builder.Services.AddSingleton<IProcessRunner, LocalSystemProcessRunner>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
builder.Services.AddSingleton<ICaptureSourceProvider, StaticCaptureSourceProvider>();
builder.Services.AddSingleton<IPluginProfileProvider, JsonPluginProfileProvider>();
builder.Services.AddSingleton<IIngestionPluginResolver, MediaPlatformIngestionPluginResolver>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
builder.Services.AddSingleton<IProcessStateRepository, InMemoryProcessStateRepository>();
builder.Services.AddSingleton<IProcessInspector, LocalProcessInspector>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton(new IncrementalSegmentationOptions
{
	SegmentDurationSeconds = options.SegmentDurationSeconds
});
builder.Services.AddSingleton(new ProcessGuardianOptions
{
	RestartTimeout = options.ProcessGuardianTimeout,
	RestartCommandTimeout = options.ProcessGuardianRestartCommandTimeout
});
builder.Services.AddSingleton<IContinuousCaptureUseCase, ContinuousCaptureUseCase>();
builder.Services.AddSingleton<IIncrementalSegmentationUseCase, IncrementalSegmentationUseCase>();
builder.Services.AddSingleton<IProcessMonitorUseCase, ProcessMonitorUseCase>();
builder.Services.AddSingleton<IReconcileInactiveUseCase, ReconcileInactiveUseCase>();
builder.Services.AddSingleton<IChunkProcessMonitorUseCase, ChunkProcessMonitorUseCase>();
builder.Services.AddSingleton<IContinuousIngestionOrchestrator, ContinuousIngestionOrchestrator>();
builder.Services.AddSingleton<IDiscreteIngestionOrchestrator, DiscreteIngestionOrchestrator>();
builder.Services.AddHostedService<ContinuousIngestionWorker>();
builder.Services.AddHostedService<DiscreteIngestionWorker>();

var host = builder.Build();
await host.RunAsync();