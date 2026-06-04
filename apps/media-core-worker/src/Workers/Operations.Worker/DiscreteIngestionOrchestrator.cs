using Microsoft.Extensions.Logging;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class DiscreteIngestionOrchestrator : IDiscreteIngestionOrchestrator
{
    private readonly ILogger<DiscreteIngestionOrchestrator> logger;

    public DiscreteIngestionOrchestrator(ILogger<DiscreteIngestionOrchestrator> logger)
    {
        this.logger = logger;
    }

    public Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Discrete ingestion cycle executed (no discrete plugins enabled yet).");
        return Task.CompletedTask;
    }
}