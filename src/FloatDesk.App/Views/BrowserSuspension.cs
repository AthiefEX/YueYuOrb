using System.Runtime.InteropServices;

namespace FloatDesk.App.Views;

public enum BrowserSuspendOutcome
{
    Suspended,
    AlreadySuspended,
    CannotSuspend,
    Unavailable
}

public static class BrowserSuspension
{
    public static async Task<BrowserSuspendOutcome> TrySuspendAsync(
        Func<bool> isSuspended,
        Func<Task<bool>> suspendAsync)
    {
        try
        {
            if (isSuspended())
                return BrowserSuspendOutcome.AlreadySuspended;
            return await suspendAsync()
                ? BrowserSuspendOutcome.Suspended
                : BrowserSuspendOutcome.CannotSuspend;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            COMException or
            ObjectDisposedException)
        {
            return BrowserSuspendOutcome.Unavailable;
        }
    }
}
