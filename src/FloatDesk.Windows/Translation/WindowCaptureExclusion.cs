using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Translation;

public static class WindowCaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public static bool TryExclude(nint hwnd) =>
        TryExclude(hwnd, SetWindowDisplayAffinity);

    public static bool TryExclude(
        nint hwnd,
        Func<nint, uint, bool> setDisplayAffinity)
    {
        ArgumentNullException.ThrowIfNull(setDisplayAffinity);
        if (hwnd == 0)
            return false;

        try
        {
            return setDisplayAffinity(
                hwnd,
                WDA_EXCLUDEFROMCAPTURE);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(
        nint hwnd,
        uint affinity);
}
