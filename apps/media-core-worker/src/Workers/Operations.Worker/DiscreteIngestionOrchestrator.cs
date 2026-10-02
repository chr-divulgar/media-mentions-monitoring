using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public sealed class DiscreteIngestionOrchestrator : IDiscreteIngestionOrchestrator
{
    private readonly ILogger<DiscreteIngestionOrchestrator> logger;
    private readonly IWebNewsCaptureUseCase? webNewsCaptureUseCase;

    public DiscreteIngestionOrchestrator(ILogger<DiscreteIngestionOrchestrator> logger, IWebNewsCaptureUseCase? webNewsCaptureUseCase = null)
    {
        this.logger = logger;
        this.webNewsCaptureUseCase = webNewsCaptureUseCase;
    }

    public async Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
    {
        if (webNewsCaptureUseCase is null)
        {
            logger.LogDebug("Discrete ingestion cycle executed (no discrete plugins enabled).");
            return;
        }

        await webNewsCaptureUseCase.ExecuteCycleAsync(cancellationToken).ConfigureAwait(false);
    }
}
