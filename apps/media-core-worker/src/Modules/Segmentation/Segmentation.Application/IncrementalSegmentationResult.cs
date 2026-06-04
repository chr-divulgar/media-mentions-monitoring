namespace MediaOpsCore.Modules.Segmentation.Application;

public sealed record IncrementalSegmentationResult(int CapturesScanned, int SegmentsGenerated, double PipelineLagSeconds);