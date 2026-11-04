using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Diagnostics;

internal static class DxgiAdapters
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDescription
    {
        internal fixed char Description[128];
        internal uint VendorId, DeviceId, SubsystemId, Revision;
        internal nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal uint LuidLow, LuidHigh, Flags;
    }

    internal static unsafe Dictionary<string, string> Enumerate()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(iid, out var factory) < 0 || factory == 0)
        {
            if (factory != 0) Marshal.Release(factory);
            return result;
        }
        try
        {
            // IDXGIFactory1::EnumAdapters1 (slot 12), IDXGIAdapter1::GetDesc1 (slot 10).
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; ; index++)
            {
                nint adapter = 0;
                var status = enumerate(factory, index, &adapter);
                if (status < 0 || adapter == 0)
                {
                    if (adapter != 0) Marshal.Release(adapter);
                    break;
                }
                try
                {
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    AdapterDescription description;
                    if (getDescription(adapter, &description) >= 0 && (description.Flags & 2) == 0)
                        result[ResourceUsageMath.AdapterId(description.LuidHigh, description.LuidLow)] = new string(description.Description);
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
}
