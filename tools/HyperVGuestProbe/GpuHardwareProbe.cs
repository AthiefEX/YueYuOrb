internal static class GpuHardwareProbe
{
    public const string Script = """
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class YueYuGpuDeviceProbe {
            [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
            public struct Desc {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description;
                public uint Vendor, Device, Subsystem, Revision;
                public ulong VideoMemory, SystemMemory, SharedMemory;
                public uint LuidLow; public int LuidHigh; public uint Flags;
            }
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            delegate int GetDesc(IntPtr adapter, out Desc description);
            [DllImport("dxgi.dll")]
            static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
            [DllImport("d3d11.dll")]
            static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
                IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);
            public static bool Run() {
                IntPtr factory=IntPtr.Zero;
                Guid iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid,out factory)<0) return false;
                try {
                    IntPtr table=Marshal.ReadIntPtr(factory);
                    var enumerate=(EnumAdapter)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table,12*IntPtr.Size),typeof(EnumAdapter));
                    for(uint i=0;i<32;i++) {
                        IntPtr adapter;
                        if(enumerate(factory,i,out adapter)<0) break;
                        try {
                            var describe=(GetDesc)Marshal.GetDelegateForFunctionPointer(
                                Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter),10*IntPtr.Size),typeof(GetDesc));
                            Desc desc;
                            if(describe(adapter,out desc)<0 || desc.Vendor!=0x10de || (desc.Flags&2)!=0) continue;
                            IntPtr device=IntPtr.Zero,context=IntPtr.Zero; uint level;
                            try {
                                int hr=D3D11CreateDevice(adapter,0,IntPtr.Zero,0,IntPtr.Zero,0,7,out device,out level,out context);
                                if(hr>=0 && level>=0xb000) return true;
                            } finally {
                                if(context!=IntPtr.Zero) Marshal.Release(context);
                                if(device!=IntPtr.Zero) Marshal.Release(device);
                            }
                        } finally { Marshal.Release(adapter); }
                    }
                    return false;
                } finally { Marshal.Release(factory); }
            }
        }
        '@
        if (-not [YueYuGpuDeviceProbe]::Run()) { exit 1 }
        """;
}
