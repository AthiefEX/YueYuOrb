namespace FloatDesk.Core.Models;

public enum ResourceMetric { Memory, Cpu, Gpu }

public sealed record ResourceUsageSettings
{
    public bool LiquidEnabled { get; init; }
    public bool RingsEnabled { get; init; }
    public double RingThicknessPercent { get; init; } = 4.3;
    public ResourceMetric LiquidMetric { get; init; } = ResourceMetric.Memory;
    public bool RingMemory { get; init; } = true;
    public bool RingCpu { get; init; } = true;
    public bool RingGpu { get; init; } = true;
    public string LiquidColor { get; init; } = "#59C4A6";
    public string MemoryColor { get; init; } = "#59C4A6";
    public string CpuColor { get; init; } = "#60AAFF";
    public string GpuColor { get; init; } = "#C69BFF";
    public bool ShowDetails { get; init; } = true;
    public double HoverDelaySeconds { get; init; } = .8;
    public string? GpuDeviceId { get; init; }
    public bool Enabled => LiquidEnabled || RingsEnabled;
}
