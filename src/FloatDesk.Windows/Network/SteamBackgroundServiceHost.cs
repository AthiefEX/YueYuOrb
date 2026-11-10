using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Network;

public static class SteamBackgroundServiceHost
{
    private delegate void MainCallback(int count, IntPtr arguments);
    private delegate void ControlCallback(uint control);
    private static MainCallback? _main;
    private static ControlCallback? _control;

    public static int Run()
    {
        _main = (_, _) =>
        {
            using var stop = new CancellationTokenSource();
            _control = control => { if (control is 1 or 5) stop.Cancel(); };
            var handle = RegisterServiceCtrlHandler(SteamBackgroundProtocol.ServiceName, _control);
            if (handle == IntPtr.Zero) return;
            var status = new Status { Type = 0x10, State = 2, Wait = 15000 };
            SetServiceStatus(handle, ref status);
            try
            {
                status.State = 4; status.Accepted = 5; status.Wait = 0; SetServiceStatus(handle, ref status);
                using var server = new SteamBackgroundServer();
                server.RunAsync(stop.Token).GetAwaiter().GetResult();
            }
            catch { status.Exit = 1; }
            finally { status.State = 1; status.Accepted = 0; SetServiceStatus(handle, ref status); }
        };
        var size = Marshal.SizeOf<Entry>(); var table = Marshal.AllocHGlobal(size * 2);
        try
        {
            Marshal.StructureToPtr(new Entry { Name = SteamBackgroundProtocol.ServiceName, Main = _main }, table, false);
            Marshal.StructureToPtr(default(Entry), IntPtr.Add(table, size), false);
            return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { Marshal.DestroyStructure<Entry>(table); Marshal.DestroyStructure<Entry>(IntPtr.Add(table, size)); Marshal.FreeHGlobal(table); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public MainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Exit, Specific, Checkpoint, Wait; }
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher(IntPtr table);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ControlCallback callback);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
}
