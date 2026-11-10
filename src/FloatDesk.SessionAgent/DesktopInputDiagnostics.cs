using System.Runtime.InteropServices;

namespace FloatDesk.SessionAgent;

public static class DesktopInputDiagnostics
{
    public static bool IsReady()
    {
        var input = OpenInputDesktop(0, false, 1);
        if (input == IntPtr.Zero)
            return false;
        try
        {
            return string.Equals(Name(input), Name(GetThreadDesktop(GetCurrentThreadId())), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(input);
        }
    }
    internal static string Describe()
    {
        var station = Name(GetProcessWindowStation());
        var desktop = Name(GetThreadDesktop(GetCurrentThreadId()));
        var input = OpenInputDesktop(0, false, 1);
        var error = Marshal.GetLastWin32Error();
        var inputName = input == IntPtr.Zero ? $"denied-{error}" : Name(input);
        if (input != IntPtr.Zero)
            CloseDesktop(input);
        return $"station={station};desktop={desktop};input={inputName}";
    }

    private static string Name(IntPtr handle)
    {
        var name = new char[256];
        return GetUserObjectInformation(handle, 2, name, 512, out _)
            ? new string(name).TrimEnd('\0') : $"error-{Marshal.GetLastWin32Error()}";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, [Out] char[] information, uint length, out uint needed);
}
