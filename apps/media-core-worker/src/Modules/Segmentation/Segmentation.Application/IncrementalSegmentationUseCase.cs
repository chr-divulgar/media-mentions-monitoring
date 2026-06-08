using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Modules.Segmentation.Application;

public sealed class IncrementalSegmentationUseCase : IIncrementalSegmentationUseCase
{
    private const string GlobalIngestionScopeId = "global-ingestion";

    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ISegmentCursorRepository segmentCursorRepository;
    private readonly IncrementalSegmentationOptions options;
    private readonly Func<DateTimeOffset> nowProvider;
    private sealed record CaptureArtifactPayload(bool Succeeded);

    public IncrementalSegmentationUseCase(
        IMonitoringArtifactRepository monitoringArtifactRepository,
        ISegmentCursorRepository segmentCursorRepository,
        IncrementalSegmentationOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.segmentCursorRepository = segmentCursorRepository;
        this.options = options;
        nowProvider = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IncrementalSegmentationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var lastProcessedAt = await segmentCursorRepository.GetLastProcessedAtAsync(GlobalIngestionScopeId, cancellationToken).ConfigureAwait(false);
        var artifacts = await monitoringArtifactRepository.ListByTenantAsync(GlobalIngestionScopeId, cancellationToken).ConfigureAwait(false);

        var captureArtifacts = artifacts
            .Where(artifact => string.Equals(artifact.Kind, "capture", StringComparison.Ordinal))
            .Where(artifact => CaptureSucceeded(artifact.PayloadJson))
            .OrderBy(artifact => artifact.CapturedAtUtc)
            .ToArray();

        var pendingCaptures = lastProcessedAt is null
            ? captureArtifacts
            : captureArtifacts.Where(artifact => artifact.CapturedAtUtc > lastProcessedAt.Value).ToArray();

        var generatedSegments = 0;
        DateTimeOffset? newestProcessedCapture = null;

        foreach (var captureArtifact in pendingCaptures)
        {
            var generatedAt = nowProvider().ToOffset(captureArtifact.CapturedAtUtc.Offset);
            var segmentPayload = JsonSerializer.Serialize(new
            {
                captureArtifactId = captureArtifact.Id,
                options.SegmentDurationSeconds,
                generatedAt = generatedAt
            });

            var segmentArtifact = new MonitoringArtifact(
                id: $"segment-{captureArtifact.Id}",
                tenantId: captureArtifact.TenantId,
                source: captureArtifact.Source,
                kind: "segment",
                payloadJson: segmentPayload,
                capturedAtUtc: captureArtifact.CapturedAtUtc);

            await monitoringArtifactRepository.UpsertAsync(segmentArtifact, cancellationToken).ConfigureAwait(false);
            generatedSegments++;
            newestProcessedCapture = captureArtifact.CapturedAtUtc;
        }

        if (newestProcessedCapture is not null)
        {
            await segmentCursorRepository
                .SaveLastProcessedAtAsync(GlobalIngestionScopeId, newestProcessedCapture.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var lagReference = newestProcessedCapture ?? lastProcessedAt;
        var pipelineLag = lagReference is null
            ? 0
            : Math.Max(0, (nowProvider().ToOffset(lagReference.Value.Offset) - lagReference.Value).TotalSeconds);

        return new IncrementalSegmentationResult(captureArtifacts.Length, generatedSegments, pipelineLag);
    }

    private static bool CaptureSucceeded(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CaptureArtifactPayload>(payloadJson);
            return payload?.Succeeded == true;
        }
        catch
        {
            return false;
        }
    }
}