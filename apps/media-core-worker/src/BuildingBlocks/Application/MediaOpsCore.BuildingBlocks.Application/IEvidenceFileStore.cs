namespace MediaOpsCore.BuildingBlocks.Application;

public interface IEvidenceFileStore
{
    Task WriteJsonAsync<T>(string relativePath, T payload, CancellationToken cancellationToken = default);
}