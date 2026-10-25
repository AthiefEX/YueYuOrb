using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Foreground;

public sealed class ForegroundMonitor : IForegroundMonitor
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly NativeMethods.WinEventDelegate _foregroundCallback;
    private readonly NativeMethods.WinEventDelegate _focusCallback;
    private nint _foregroundHook;
    private nint _focusHook;
    private long _epoch;
    private bool _disposed;

    public ForegroundMonitor()
    {
        _foregroundCallback = OnWinEvent;
        _focusCallback = OnWinEvent;
        Current = EmptySnapshot();
    }

    public event Action<ForegroundSnapshot>? Changed;
    public ForegroundSnapshot Current { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_foregroundHook != 0)
            return;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            0,
            _foregroundCallback,
            0,
            0,
            NativeMethods.WineventOutOfContext);
        _focusHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectFocus,
            NativeMethods.EventObjectFocus,
            0,
            _focusCallback,
            0,
            0,
            NativeMethods.WineventOutOfContext);
        if (_foregroundHook == 0 || _focusHook == 0)
            throw new InvalidOperationException("Unable to install foreground event hooks.");
    }

    public async Task<ForegroundSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var candidate = BuildSnapshot(cancellationToken);
            var changed = !HasSameIdentity(Current, candidate);
            if (changed)
            {
                candidate = candidate with { Epoch = Interlocked.Increment(ref _epoch) };
                Current = candidate;
            }
            else
            {
                candidate = Current;
            }

            if (changed)
                Changed?.Invoke(candidate);
            return candidate;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private ForegroundSnapshot BuildSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var top = NativeMethods.GetForegroundWindow();
        if (top == 0)
            return EmptySnapshot() with { Epoch = _epoch };
        var threadId = NativeMethods.GetWindowThreadProcessId(top, out var processId);
        var focus = top;
        var info = new NativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        if (threadId != 0 &&
            NativeMethods.GetGUIThreadInfo(threadId, ref info) &&
            info.FocusWindow != 0)
        {
            focus = info.FocusWindow;
        }

        string? path = null;
        DateTimeOffset? start = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            path = process.MainModule?.FileName;
            start = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            NotSupportedException or
            ArgumentException)
        {
            // Protected/elevated processes often do not expose their path.
        }

        return new ForegroundSnapshot(
            _epoch,
            top,
            focus,
            processId,
            threadId,
            start,
            path,
            processId == Environment.ProcessId);
    }

    public static bool HasSameIdentity(
        ForegroundSnapshot first,
        ForegroundSnapshot second) =>
        first.TopLevelHwnd == second.TopLevelHwnd &&
        first.FocusHwnd == second.FocusHwnd &&
        first.ProcessId == second.ProcessId &&
        first.ThreadId == second.ThreadId &&
        first.ProcessStartUtc == second.ProcessStartUtc &&
        string.Equals(first.ExePath, second.ExePath, StringComparison.OrdinalIgnoreCase) &&
        first.IsOwnInteractive == second.IsOwnInteractive;

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch
            {
                // A transient window teardown must not stop the event hook.
            }
        });
    }

    private static ForegroundSnapshot EmptySnapshot() =>
        new(0, 0, 0, 0, 0, null, null, false);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_foregroundHook != 0)
            NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_focusHook != 0)
            NativeMethods.UnhookWinEvent(_focusHook);
        _foregroundHook = 0;
        _focusHook = 0;
        _refreshGate.Dispose();
    }
}
