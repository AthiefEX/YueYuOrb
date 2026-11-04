namespace FloatDesk.Core.Models;

public sealed record GpuUsage(string Id, string Name, double? Percent);

public sealed record ResourceUsageSnapshot(double? CpuPercent, double? MemoryPercent,
    ulong UsedMemoryBytes, ulong TotalMemoryBytes, IReadOnlyList<GpuUsage> Gpus)
{
    public static ResourceUsageSnapshot Empty { get; } = new(null, null, 0, 0, []);
    public GpuUsage? SelectGpu(string? id) => id is null
        ? Gpus.OrderByDescending(g => g.Percent ?? -1).FirstOrDefault()
        : Gpus.FirstOrDefault(g => g.Id == id);
    public double? Get(ResourceMetric metric, string? gpuId) => metric switch
    {
        ResourceMetric.Cpu => CpuPercent,
        ResourceMetric.Gpu => SelectGpu(gpuId)?.Percent,
        _ => MemoryPercent
    };
}
