using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Network;

public sealed class WinInetProxyConfiguration : IProxyConfigurationStore
{
    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)] public int Flags;
        [FieldOffset(0)] public nint Text;
        [FieldOffset(0)] public long FileTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Option { public int Kind; public OptionValue Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public nint Connection;
        public int Count;
        public int Error;
        public nint Options;
    }

    public ProxyConfiguration Read() => Access(null);
    public void Write(ProxyConfiguration configuration) => Access(configuration);

    private static ProxyConfiguration Access(ProxyConfiguration? configuration)
    {
        var optionSize = Marshal.SizeOf<Option>();
        var options = Marshal.AllocHGlobal(optionSize * 2);
        var url = configuration is null ? nint.Zero : Marshal.StringToHGlobalUni(configuration.AutoConfigUrl);
        nint resultUrl = 0;
        try
        {
            Marshal.StructureToPtr(new Option { Kind = 1, Value = new() { Flags = configuration?.Flags ?? 0 } }, options, false);
            Marshal.StructureToPtr(new Option { Kind = 4, Value = new() { Text = url } }, options + optionSize, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 2, Options = options };
            if (configuration is null)
            {
                var size = list.Size;
                if (!InternetQueryOption(0, 75, ref list, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var flags = Marshal.PtrToStructure<Option>(options).Value.Flags;
                resultUrl = Marshal.PtrToStructure<Option>(options + optionSize).Value.Text;
                return new(flags, Marshal.PtrToStringUni(resultUrl) ?? "");
            }
            if (!InternetSetOption(0, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            InternetSetOptionNotify(0, 39, 0, 0);
            InternetSetOptionNotify(0, 37, 0, 0);
            return configuration;
        }
        finally
        {
            if (resultUrl != 0) GlobalFree(resultUrl);
            if (url != 0) Marshal.FreeHGlobal(url);
            Marshal.FreeHGlobal(options);
        }
    }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(nint internet, int option, ref OptionList buffer, ref int length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(nint internet, int option, ref OptionList buffer, int length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOptionNotify(nint internet, int option, nint buffer, int length);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
