using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Modules.Segmentation.Application;

public sealed class IncrementalSegmentationUseCase : IIncrementalSegmentationUseCase
{
    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ISegmentCursorRepository segmentCursorRepository;
    private readonly IncrementalSegmentationOptions options;
    private readonly Func<DateTimeOffset> utcNow;

    public IncrementalSegmentationUseCase(
        IMonitoringArtifactRepository monitoringArtifactRepository,
        ISegmentCursorRepository segmentCursorRepository,
        IncrementalSegmentationOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.segmentCursorRepository = segmentCursorRepository;
        this.options = options;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IncrementalSegmentationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            throw new InvalidOperationException("TenantId cannot be empty.");
        }

        var lastProcessedAt = await segmentCursorRepository.GetLastProcessedAtAsync(options.TenantId, cancellationToken).ConfigureAwait(false);
        var artifacts = await monitoringArtifactRepository.ListByTenantAsync(options.TenantId, cancellationToken).ConfigureAwait(false);

        var captureArtifacts = artifacts
            .Where(artifact => string.Equals(artifact.Kind, "capture", StringComparison.Ordinal))
            .OrderBy(artifact => artifact.CapturedAtUtc)
            .ToArray();

        var pendingCaptures = lastProcessedAt is null
            ? captureArtifacts
            : captureArtifacts.Where(artifact => artifact.CapturedAtUtc > lastProcessedAt.Value).ToArray();

        var generatedSegments = 0;
        DateTimeOffset? newestProcessedCapture = null;

        foreach (var captureArtifact in pendingCaptures)
        {
            var segmentPayload = JsonSerializer.Serialize(new
            {
                captureArtifactId = captureArtifact.Id,
                options.SegmentDurationSeconds,
                generatedAtUtc = utcNow()
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
                .SaveLastProcessedAtAsync(options.TenantId, newestProcessedCapture.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var lagReference = newestProcessedCapture ?? lastProcessedAt;
        var pipelineLag = lagReference is null
            ? 0
            : Math.Max(0, (utcNow() - lagReference.Value).TotalSeconds);

        return new IncrementalSegmentationResult(captureArtifacts.Length, generatedSegments, pipelineLag);
    }
}