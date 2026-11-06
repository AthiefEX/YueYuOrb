using System.Runtime.InteropServices;

namespace FloatDesk.SessionBroker;

internal static class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceStopHandler? _serviceHandler;
    private static CancellationTokenSource? _stopCancellation;

    public static int Run(
        string serviceName,
        Func<CancellationToken, Task> runAsync)
    {
        _serviceMain = (_, _) =>
            RunServiceMain(serviceName, runAsync);
        var entrySize = Marshal.SizeOf<ServiceTableEntry>();
        var table = Marshal.AllocHGlobal(entrySize * 2);
        try
        {
            Marshal.StructureToPtr(
                new ServiceTableEntry(serviceName, _serviceMain),
                table,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                default(ServiceTableEntry),
                IntPtr.Add(table, entrySize),
                fDeleteOld: false);
            return StartServiceCtrlDispatcher(table)
                ? 0
                : Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.DestroyStructure<ServiceTableEntry>(table);
            Marshal.DestroyStructure<ServiceTableEntry>(
                IntPtr.Add(table, entrySize));
            Marshal.FreeHGlobal(table);
            _serviceMain = null;
        }
    }

    private static void RunServiceMain(
        string serviceName,
        Func<CancellationToken, Task> runAsync)
    {
        using var cancellation = new CancellationTokenSource();
        _stopCancellation = cancellation;
        _serviceHandler = control =>
        {
            if (control == ServiceControlStop)
                cancellation.Cancel();
        };
        var handlerPointer = RegisterServiceCtrlHandler(
            serviceName,
            _serviceHandler);
        if (handlerPointer == IntPtr.Zero)
            return;

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = ServiceStartPending,
            ControlsAccepted = ServiceAcceptStop,
            CheckPoint = 1
        };
        _ = SetServiceStatus(handlerPointer, ref status);
        try
        {
            status.CurrentState = ServiceRunning;
            status.CheckPoint = 0;
            _ = SetServiceStatus(handlerPointer, ref status);
            runAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch
        {
            status.Win32ExitCode = 1;
        }
        finally
        {
            status.CurrentState = ServiceStopped;
            status.ControlsAccepted = 0;
            status.CheckPoint = 0;
            _ = SetServiceStatus(handlerPointer, ref status);
            _stopCancellation = null;
            _serviceHandler = null;
        }
    }

    private delegate void ServiceMainDelegate(
        int argumentCount,
        IntPtr arguments);

    private delegate void ServiceStopHandler(uint control);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public ServiceTableEntry(string name, ServiceMainDelegate callback)
        {
            Name = name;
            Callback = callback;
        }

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;
        public ServiceMainDelegate? Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public int Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "StartServiceCtrlDispatcherW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(
        IntPtr table);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "RegisterServiceCtrlHandlerW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandler(
        string serviceName,
        ServiceStopHandler handler);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(
        IntPtr handle,
        ref ServiceStatus status);
}
