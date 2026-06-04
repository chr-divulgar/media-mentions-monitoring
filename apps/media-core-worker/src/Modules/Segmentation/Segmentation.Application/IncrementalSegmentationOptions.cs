namespace MediaOpsCore.Modules.Segmentation.Application;

public sealed class IncrementalSegmentationOptions
{
    public string TenantId { get; set; } = "default";

    public int SegmentDurationSeconds { get; set; } = 30;
}