using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Equivalent to Helper.GetClients / Helper.GetPlatforms (media-monitor/apps/w-service/Helper.cs:452-462) —
// reads the same config.client / config.platform collections the legacy service already reads.
public interface IClientConfigRepository
{
    Task<IReadOnlyList<ClientKeywordConfig>> GetClientsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPlatformNamesAsync(CancellationToken cancellationToken = default);
}
