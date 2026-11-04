using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Diagnostics;

internal sealed class GpuPerformanceQuery : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint Format = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    private PdhQueryHandle? _query;
    private nint _counter;
    private bool _primed;

    internal Dictionary<string, double?> Sample()
    {
        if (_query is null)
        {
            var status = ResourceUsageNative.PdhOpenQueryW(null, 0, out var query);
            if (status != 0 || query.IsInvalid)
            {
                query.Dispose();
                return [];
            }
            _query = query;
            if (ResourceUsageNative.PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
            {
                Reset();
                return [];
            }
        }
        if (ResourceUsageNative.PdhCollectQueryData(_query) != 0)
        {
            Reset();
            return [];
        }
        if (!_primed)
        {
            _primed = true;
            return [];
        }
        // Re-query size on every retry: an undersized call does not reliably return a new size.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            var status = ResourceUsageNative.PdhGetFormattedCounterArrayW(_counter, Format, ref bytes, out _, 0);
            if (status != MoreData || bytes == 0) return [];
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = ResourceUsageNative.PdhGetFormattedCounterArrayW(_counter, Format, ref bytes, out var count, buffer);
                if (status == MoreData) continue;
                if (status != 0) return [];
                var samples = new List<GpuEngineSample>();
                var stride = Marshal.SizeOf<ResourceUsageNative.CounterItem>();
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<ResourceUsageNative.CounterItem>(buffer + checked((int)i * stride));
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name is not null)
                        samples.Add(new(name, item.Value.Status is 0 or 1 ? item.Value.Value : null));
                }
                return ResourceUsageMath.AggregateGpu(samples);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    internal void Reset()
    {
        _query?.Dispose(); // Closing the query also releases its counters.
        _query = null;
        _counter = 0;
        _primed = false;
    }

    public void Dispose() => Reset();
}
