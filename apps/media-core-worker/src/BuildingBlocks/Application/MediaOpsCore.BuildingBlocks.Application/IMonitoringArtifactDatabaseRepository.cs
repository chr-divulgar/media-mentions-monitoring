using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.BuildingBlocks.Application;

// Marker contract for optional DB-backed evidence sinks.
public interface IMonitoringArtifactDatabaseRepository : IMonitoringArtifactRepository
{
}
