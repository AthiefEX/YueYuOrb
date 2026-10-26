using System.ComponentModel;
using System.Runtime.InteropServices;
using FloatDesk.Core.Input;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Native;
using Microsoft.Win32;

namespace FloatDesk.Windows.Input;

public sealed class WindowsKeyboardSwitchGuard : IKeyboardSwitchGuard
{
    private readonly object _sync = new();
    private readonly KeyboardSwitchGuardState _state = new();
    private HookSession? _session;
    private bool _disposed;

    public bool IsActive
    {
        get
        {
            lock (_sync)
                return _session?.IsRunning == true;
        }
    }

    public OperationResult Enable(
        InputLayoutTarget target,
        IReadOnlyList<KeyboardSwitchChord>? additionalShortcuts = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configured = ReadConfiguredShortcuts()
            .Concat(additionalShortcuts ?? [])
            .ToArray();

        lock (_sync)
        {
            _state.Begin(target, configured);
            if (_session?.IsRunning == true)
                return OperationResult.Ok();

            var session = new HookSession(_state);
            var enabled = session.Start();
            if (!enabled.Success)
            {
                session.Dispose();
                _state.End();
                return enabled;
            }
            _session = session;
            return OperationResult.Ok();
        }
    }

    public void Disable()
    {
        HookSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        session?.Dispose();
        _state.End();
    }

    public void ObserveLayout(LayoutObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _state.ObserveLayout(observation);
    }

    public void ClearPendingLearning() => _state.ClearPendingLearning();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disable();
    }

    private static IReadOnlyList<KeyboardSwitchChord>
        ReadConfiguredShortcuts()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Keyboard Layout\Toggle");
            if (key is null)
                return KeyboardSwitchShortcuts.Defaults;
            var values = key.GetValueNames()
                .Select(name => new KeyValuePair<string, string?>(
                    name,
                    key.GetValue(name)?.ToString()))
                .ToArray();
            return KeyboardSwitchShortcuts.ParseToggleValues(values);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            System.Security.SecurityException or
            IOException)
        {
            return KeyboardSwitchShortcuts.Defaults;
        }
    }

    private sealed class HookSession : IDisposable
    {
        private readonly KeyboardSwitchGuardState _state;
        private readonly NativeMethods.LowLevelKeyboardProc _callback;
        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;
        private nint _hook;
        private uint _threadId;
        private OperationResult _startResult =
            OperationResult.Fail("HookUnavailable", "键盘钩子尚未启动。");
        private volatile bool _running;
        private int _disposed;

        internal HookSession(KeyboardSwitchGuardState state)
        {
            _state = state;
            _callback = OnKeyboardMessage;
        }

        internal bool IsRunning => _running;

        internal OperationResult Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FloatDesk.InputShortcutGuard"
            };
            try
            {
                _thread.Start();
            }
            catch (Exception exception)
            {
                _thread = null;
                return OperationResult.Fail(
                    "HookUnavailable",
                    $"无法启动键盘快捷键拦截线程：{exception.Message}");
            }
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                if (_threadId != 0)
                {
                    NativeMethods.PostThreadMessage(
                        _threadId,
                        NativeMethods.WmQuit,
                        0,
                        0);
                }
                _thread.Join(TimeSpan.FromMilliseconds(500));
                return OperationResult.Fail(
                    "HookTimeout",
                    "键盘快捷键拦截线程启动超时。");
            }
            return _startResult;
        }

        private void Run()
        {
            try
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                NativeMethods.PeekMessage(
                    out _,
                    0,
                    0,
                    0,
                    NativeMethods.PmNoRemove);
                var module = NativeMethods.GetModuleHandle(null);
                _hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhKeyboardLl,
                    _callback,
                    module,
                    0);
                if (_hook == 0)
                {
                    var error = new Win32Exception(
                        Marshal.GetLastWin32Error());
                    _startResult = OperationResult.Fail(
                        "HookUnavailable",
                        $"无法启动全局键盘快捷键拦截：{error.Message}");
                    _ready.Set();
                    return;
                }

                _running = true;
                _startResult = OperationResult.Ok();
                _ready.Set();
                while (true)
                {
                    var result = NativeMethods.GetMessage(
                        out var message,
                        0,
                        0,
                        0);
                    if (result <= 0)
                        break;
                    NativeMethods.TranslateMessage(ref message);
                    NativeMethods.DispatchMessage(ref message);
                }
            }
            catch (Exception exception)
            {
                if (!_ready.IsSet)
                {
                    _startResult = OperationResult.Fail(
                        "HookUnavailable",
                        $"无法启动全局键盘快捷键拦截：{exception.Message}");
                    _ready.Set();
                }
            }
            finally
            {
                _running = false;
                if (_hook != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(_hook);
                    _hook = 0;
                }
                _ready.Set();
            }
        }

        private nint OnKeyboardMessage(
            int code,
            nint wordParameter,
            nint longParameter)
        {
            try
            {
                if (code >= 0 && longParameter != 0)
                {
                    var message = (uint)wordParameter;
                    if (message is
                        NativeMethods.WmKeyDown or
                        NativeMethods.WmKeyUp or
                        NativeMethods.WmSysKeyDown or
                        NativeMethods.WmSysKeyUp)
                    {
                        var data = Marshal.PtrToStructure<
                            NativeMethods.KeyboardHookData>(
                            longParameter);
                        var isDown = message is
                            NativeMethods.WmKeyDown or
                            NativeMethods.WmSysKeyDown;
                        if (_state.HandleKeyEvent(data.VirtualKey, isDown))
                            return 1;
                    }
                }
            }
            catch
            {
                // A hook exception must never break the global input path.
            }

            return NativeMethods.CallNextHookEx(
                _hook,
                code,
                wordParameter,
                longParameter);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_threadId != 0)
            {
                NativeMethods.PostThreadMessage(
                    _threadId,
                    NativeMethods.WmQuit,
                    0,
                    0);
            }
            var joined = _thread?.Join(TimeSpan.FromSeconds(2)) ?? true;
            if (!joined && _hook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
            _running = false;
            if (joined)
                _ready.Dispose();
        }
    }
}
