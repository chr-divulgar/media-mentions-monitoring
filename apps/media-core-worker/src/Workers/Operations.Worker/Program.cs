using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Segmentation.Application;
using MediaOpsCore.Workers.Operations;

var builder = Host.CreateApplicationBuilder(args);

var options = new OperationsWorkerOptions();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IMonitoringArtifactRepository, InMemoryMonitoringArtifactRepository>();
builder.Services.AddSingleton<IProcessRunner, LocalSystemProcessRunner>();
builder.Services.AddSingleton<IOperationalMetrics, MeterOperationalMetrics>();
builder.Services.AddSingleton<ICaptureSourceProvider, StaticCaptureSourceProvider>();
builder.Services.AddSingleton<ISegmentCursorRepository, InMemorySegmentCursorRepository>();
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
builder.Services.AddSingleton<IContinuousCaptureUseCase, ContinuousCaptureUseCase>();
builder.Services.AddSingleton<IIncrementalSegmentationUseCase, IncrementalSegmentationUseCase>();
builder.Services.AddHostedService<OperationsWorker>();

var host = builder.Build();
await host.RunAsync();