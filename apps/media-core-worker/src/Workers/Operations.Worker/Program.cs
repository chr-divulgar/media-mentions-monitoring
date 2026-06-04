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
builder.Services.AddSingleton<CanaryRolloutTuner>();
builder.Services.AddSingleton<InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<IMonitoringArtifactRepository, StageMirrorMonitoringArtifactRepository>();
builder.Services.AddSingleton<IProcessRunner, LocalSystemProcessRunner>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
builder.Services.AddSingleton<IEvidenceFileStore, FileSystemEvidenceStore>();
builder.Services.AddSingleton<ILegacySnapshotProvider, JsonLegacySnapshotProvider>();
builder.Services.AddSingleton<ICaptureSourceProvider, StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
builder.Services.AddSingleton<IProcessStateRepository, InMemoryProcessStateRepository>();
builder.Services.AddSingleton<IProcessInspector, LocalProcessInspector>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton(new ContinuousCaptureOptions
{
	ToolExecutable = options.CaptureToolExecutable,
	ToolArgumentsTemplate = options.CaptureToolArgumentsTemplate,
	CommandTimeout = options.CaptureCommandTimeout
});
builder.Services.AddSingleton(new IncrementalSegmentationOptions
{
	TenantId = options.TenantId,
	SegmentDurationSeconds = options.SegmentDurationSeconds
});
builder.Services.AddSingleton(new ProcessGuardianOptions
{
	RestartTimeout = options.ProcessGuardianTimeout,
	RestartCommandTimeout = options.ProcessGuardianRestartCommandTimeout
});
builder.Services.AddSingleton(new FunctionalParityOptions
{
	TenantId = options.TenantId,
	MinimumParityPercent = options.ShadowParityMinimumPercent
});
builder.Services.AddSingleton<IContinuousCaptureUseCase, ContinuousCaptureUseCase>();
builder.Services.AddSingleton<IIncrementalSegmentationUseCase, IncrementalSegmentationUseCase>();
builder.Services.AddSingleton<IProcessMonitorUseCase, ProcessMonitorUseCase>();
builder.Services.AddSingleton<IReconcileInactiveUseCase, ReconcileInactiveUseCase>();
builder.Services.AddSingleton<IChunkProcessMonitorUseCase, ChunkProcessMonitorUseCase>();
builder.Services.AddSingleton<IFunctionalParityUseCase, FunctionalParityUseCase>();
builder.Services.AddHostedService<OperationsWorker>();

var host = builder.Build();
await host.RunAsync();