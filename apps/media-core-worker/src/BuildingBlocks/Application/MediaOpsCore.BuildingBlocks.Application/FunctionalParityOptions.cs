namespace MediaOpsCore.BuildingBlocks.Application;

public sealed class FunctionalParityOptions
{
    public string TenantId { get; set; } = "default";

    public double MinimumParityPercent { get; set; } = 95;
}