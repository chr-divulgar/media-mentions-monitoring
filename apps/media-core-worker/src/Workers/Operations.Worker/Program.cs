using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MediaOpsCore.Workers.Operations;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new OperationsWorkerOptions());
builder.Services.AddHostedService<OperationsWorker>();

var host = builder.Build();
host.Run();