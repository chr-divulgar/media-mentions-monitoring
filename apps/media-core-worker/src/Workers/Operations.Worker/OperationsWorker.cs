using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class OperationsWorker : BackgroundService
{
    private readonly ILogger<OperationsWorker> logger;
    private readonly OperationsWorkerOptions options;

    public OperationsWorker(ILogger<OperationsWorker> logger, OperationsWorkerOptions options)
    {
        this.logger = logger;
        this.options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operations worker started with heartbeat interval {HeartbeatInterval}.", options.HeartbeatInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Operations worker heartbeat.");
            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }
}