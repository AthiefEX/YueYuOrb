using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Diagnostics;

/// <summary>Native system sampling. Call periodically from a background worker; no UI thread is required.</summary>
public sealed class ResourceUsageSampler : IDisposable
{
    private readonly object _gate = new();
    private readonly CpuUsageDelta _cpu = new();
    private readonly GpuPerformanceQuery _gpu = new();
    private bool _disposed;

    public ResourceUsageSnapshot Sample(bool includeGpu = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cpu = _cpu.Update(ResourceUsageNative.GetSystemTimes(out var idle, out var kernel, out var user)
                ? new CpuTimes(idle, kernel, user) : null);
            var memory = new ResourceUsageNative.MemoryStatus
            {
                Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ResourceUsageNative.MemoryStatus>()
            };
            ulong total = 0, used = 0;
            double? memoryPercent = null;
            if (ResourceUsageNative.GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 &&
                memory.AvailablePhysical <= memory.TotalPhysical)
            {
                total = memory.TotalPhysical;
                used = total - memory.AvailablePhysical;
                memoryPercent = 100d * used / total;
            }
            IReadOnlyList<GpuUsage> gpus = [];
            if (includeGpu)
            {
                var adapters = DxgiAdapters.Enumerate();
                var values = _gpu.Sample();
                // Include counters even when DXGI discovery fails, retaining a stable LUID identity.
                foreach (var id in values.Keys) adapters.TryAdd(id, id);
                gpus = adapters.Select(a => new GpuUsage(a.Key, a.Value, values.GetValueOrDefault(a.Key))).ToArray();
            }
            else _gpu.Reset();
            return new(cpu, memoryPercent, used, total, gpus);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _gpu.Dispose();
        }
    }
}
