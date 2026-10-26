using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Translation;

public sealed record SelectedTextReadResult(
    string? Text,
    string? ErrorCode,
    string? Message)
{
    public bool Success => ErrorCode is null && Text is not null;

    public static SelectedTextReadResult Ok(string text) =>
        new(text, null, null);

    public static SelectedTextReadResult Fail(
        string errorCode,
        string message) =>
        new(null, errorCode, message);
}

public interface ISelectedTextReader
{
    Task<SelectedTextReadResult> ReadAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken);
}

public sealed class SelectedTextReader : ISelectedTextReader, IDisposable
{
    private static readonly TimeSpan DefaultCopyWaitTimeout =
        TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultModifierReleaseTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(25);

    private readonly IClipboardSession _clipboard;
    private readonly ICopyShortcutSender _sender;
    private readonly IHotkeyModifierState _modifiers;
    private readonly IForegroundActivator _foreground;
    private readonly TimeSpan _copyWaitTimeout;
    private readonly TimeSpan _modifierReleaseTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly ISelectedTextPolling _polling;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;

    public SelectedTextReader()
        : this(
            new WpfClipboardSession(),
            new Win32CopyShortcutSender(),
            new Win32HotkeyModifierState(),
            new Win32ForegroundActivator(),
            DefaultCopyWaitTimeout,
            DefaultModifierReleaseTimeout,
            DefaultPollInterval,
            new SystemSelectedTextPolling())
    {
    }

    internal SelectedTextReader(
        IClipboardSession clipboard,
        ICopyShortcutSender sender,
        IHotkeyModifierState modifiers,
        IForegroundActivator foreground,
        TimeSpan copyWaitTimeout,
        TimeSpan? modifierReleaseTimeout = null,
        TimeSpan? pollInterval = null,
        ISelectedTextPolling? polling = null)
    {
        _clipboard = clipboard ??
            throw new ArgumentNullException(nameof(clipboard));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _modifiers = modifiers ??
            throw new ArgumentNullException(nameof(modifiers));
        _foreground = foreground ??
            throw new ArgumentNullException(nameof(foreground));
        _copyWaitTimeout = copyWaitTimeout;
        _modifierReleaseTimeout =
            modifierReleaseTimeout ?? DefaultModifierReleaseTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _polling = polling ?? new SystemSelectedTextPolling();

        if (_copyWaitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(copyWaitTimeout));
        if (_modifierReleaseTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(modifierReleaseTimeout));
        }
        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task<SelectedTextReadResult> ReadAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _operationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = CaptureTargetIdentity(target);
            if (identity is null)
                return TargetNotForeground();

            if (!IsForeground(identity))
            {
                if (!await TryActivateAsync(
                        identity,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return TargetNotForeground();
                }
                if (!IsForeground(identity))
                    return TargetNotForeground();
            }

            if (!await WaitForModifierReleaseAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return ModifiersHeld();
            }

            if (!IsForeground(identity))
            {
                if (!await TryActivateAsync(
                        identity,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return TargetNotForeground();
                }
                if (!IsForeground(identity))
                    return TargetNotForeground();
            }

            ClipboardSessionSnapshot? snapshot = null;
            try
            {
                try
                {
                    snapshot = await _clipboard.CaptureAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return ClipboardDenied();
                }

                if (snapshot is null)
                    return ClipboardDenied();

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsForeground(identity))
                {
                    if (!await TryActivateAsync(
                            identity,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return TargetNotForeground();
                    }
                    if (!IsForeground(identity))
                        return TargetNotForeground();
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!TrySendCopy())
                    return NoSelectedText();

                ClipboardTextReadResult readResult;
                try
                {
                    readResult = await _clipboard.WaitForTextAsync(
                            snapshot.SequenceNumber,
                            _copyWaitTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return ClipboardDenied();
                }

                if (readResult.Denied)
                    return ClipboardDenied();
                if (!readResult.SequenceChanged ||
                    string.IsNullOrWhiteSpace(readResult.Text))
                {
                    return NoSelectedText();
                }

                return SelectedTextReadResult.Ok(readResult.Text);
            }
            finally
            {
                if (snapshot is not null)
                {
                    try
                    {
                        await _clipboard.RestoreAsync(snapshot)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Restoration is best-effort and cannot replace a
                        // successfully read selection or leak details.
                    }

                    try
                    {
                        snapshot.Dispose();
                    }
                    catch
                    {
                        // Clipboard payload cleanup is best-effort.
                    }
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private ForegroundTargetIdentity? CaptureTargetIdentity(
        ForegroundSnapshot target)
    {
        try
        {
            return _foreground.CaptureIdentity(target);
        }
        catch
        {
            return null;
        }
    }

    private bool IsForeground(ForegroundTargetIdentity identity)
    {
        try
        {
            return _foreground.IsTargetForeground(identity);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryActivateAsync(
        ForegroundTargetIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _foreground.TryActivateAsync(
                    identity,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySendCopy()
    {
        try
        {
            return _sender.TrySendCopy();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForModifierReleaseAsync(
        CancellationToken cancellationToken)
    {
        var startedAt = _polling.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool held;
            try
            {
                held = _modifiers.AreAnyModifiersHeld();
            }
            catch
            {
                return false;
            }

            if (!held)
                return true;

            var elapsed = _polling.GetTimestamp() - startedAt;
            var remaining = _modifierReleaseTimeout - elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            var delay = remaining < _pollInterval
                ? remaining
                : _pollInterval;
            await _polling.DelayAsync(delay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static SelectedTextReadResult NoSelectedText() =>
        SelectedTextReadResult.Fail(
            "NoSelectedText",
            "未检测到可翻译的选中文字。");

    private static SelectedTextReadResult ClipboardDenied() =>
        SelectedTextReadResult.Fail(
            "ClipboardDenied",
            "无法读取或恢复剪贴板，请稍后重试。");

    private static SelectedTextReadResult ModifiersHeld() =>
        SelectedTextReadResult.Fail(
            "ModifiersHeld",
            "请松开 Ctrl、Alt、Shift 或 Win 键后重试。");

    private static SelectedTextReadResult TargetNotForeground() =>
        SelectedTextReadResult.Fail(
            "TargetNotForeground",
            "目标窗口已变化或无法切换至前台。");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _operationGate.Dispose();
    }
}

internal interface ISelectedTextPolling
{
    TimeSpan GetTimestamp();
    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken);
}

internal sealed class SystemSelectedTextPolling : ISelectedTextPolling
{
    public TimeSpan GetTimestamp() =>
        TimeSpan.FromSeconds(
            Stopwatch.GetTimestamp() /
            (double)Stopwatch.Frequency);

    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed record ForegroundTargetIdentity(
    nint Hwnd,
    uint ProcessId,
    uint ThreadId,
    DateTimeOffset? ProcessStartUtc,
    string? ExePath,
    string Title);

internal interface IForegroundActivator
{
    ForegroundTargetIdentity? CaptureIdentity(
        ForegroundSnapshot target);

    bool IsTargetForeground(
        ForegroundTargetIdentity identity);

    Task<bool> TryActivateAsync(
        ForegroundTargetIdentity identity,
        CancellationToken cancellationToken);
}

internal interface IHotkeyModifierState
{
    bool AreAnyModifiersHeld();
}

internal interface ICopyShortcutSender
{
    bool TrySendCopy();
}

internal sealed record ClipboardFormatSnapshot(
    string Format,
    object? Data,
    bool OwnsData = false);

internal sealed class ClipboardSessionSnapshot : IDisposable
{
    private int _disposed;

    public ClipboardSessionSnapshot(
        uint sequenceNumber,
        IReadOnlyList<ClipboardFormatSnapshot> formats)
    {
        SequenceNumber = sequenceNumber;
        Formats = formats ??
            throw new ArgumentNullException(nameof(formats));
    }

    public uint SequenceNumber { get; }
    public IReadOnlyList<ClipboardFormatSnapshot> Formats { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var format in Formats)
        {
            if (!format.OwnsData ||
                format.Data is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Snapshot disposal is best-effort.
            }
        }
    }
}

internal readonly record struct ClipboardTextReadResult(
    bool Denied,
    bool SequenceChanged,
    string? Text)
{
    public static ClipboardTextReadResult WithText(string text) =>
        new(false, true, text);

    public static ClipboardTextReadResult WithoutText(bool sequenceChanged) =>
        new(false, sequenceChanged, null);

    public static ClipboardTextReadResult AccessDenied() =>
        new(true, false, null);
}

internal interface IClipboardSession
{
    Task<ClipboardSessionSnapshot?> CaptureAsync(
        CancellationToken cancellationToken);

    Task<ClipboardTextReadResult> WaitForTextAsync(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task RestoreAsync(ClipboardSessionSnapshot snapshot);
}

internal sealed class WpfClipboardSession : IClipboardSession
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CaptureRetryDelay =
        TimeSpan.FromMilliseconds(15);
    private const int CaptureAttempts = 3;

    private readonly object _sync = new();
    private ClipboardStaWorker? _worker;

    public async Task<ClipboardSessionSnapshot?> CaptureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var worker = EnsureWorker();
        try
        {
            var snapshot = await worker.InvokeAsync(
                    () => CaptureOnSta(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
                StopWorker(worker);
            return snapshot;
        }
        catch
        {
            StopWorker(worker);
            throw;
        }
    }

    public async Task<ClipboardTextReadResult> WaitForTextAsync(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var worker = EnsureWorker();
        return await worker.InvokeAsync(
                () => WaitForTextOnSta(
                    baselineSequenceNumber,
                    timeout,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RestoreAsync(ClipboardSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClipboardStaWorker? worker;
        lock (_sync)
        {
            worker = _worker;
            _worker = null;
        }

        worker ??= new ClipboardStaWorker();
        try
        {
            await worker.InvokeAsync(
                    () =>
                    {
                        RestoreOnSta(snapshot);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            worker.Stop();
        }
    }

    private ClipboardStaWorker EnsureWorker()
    {
        lock (_sync)
        {
            return _worker ??= new ClipboardStaWorker();
        }
    }

    private void StopWorker(ClipboardStaWorker worker)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_worker, worker))
                _worker = null;
        }
        worker.Stop();
    }

    private static ClipboardSessionSnapshot? CaptureOnSta(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint sequenceBefore;
            try
            {
                sequenceBefore = GetClipboardSequenceNumber();
            }
            catch
            {
                return null;
            }

            List<ClipboardFormatSnapshot>? formats = null;
            try
            {
                var dataObject = Clipboard.GetDataObject();
                formats = dataObject is null
                    ? new List<ClipboardFormatSnapshot>()
                    : CaptureFormats(dataObject, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var sequenceAfter = GetClipboardSequenceNumber();
                if (sequenceBefore == sequenceAfter)
                {
                    return new ClipboardSessionSnapshot(
                        sequenceAfter,
                        formats);
                }
            }
            catch (OperationCanceledException)
            {
                DisposeFormats(formats);
                throw;
            }
            catch
            {
                DisposeFormats(formats);
                return null;
            }

            DisposeFormats(formats);
            if (attempt + 1 < CaptureAttempts)
                Thread.Sleep(CaptureRetryDelay);
        }

        return null;
    }

    private static List<ClipboardFormatSnapshot> CaptureFormats(
        IDataObject dataObject,
        CancellationToken cancellationToken)
    {
        var formatNames = new SortedSet<string>(
            StringComparer.Ordinal);
        Exception? formatsFailure = null;
        try
        {
            foreach (var format in dataObject.GetFormats(false))
                formatNames.Add(format);
        }
        catch (Exception exception)
        {
            formatsFailure = exception;
        }

        try
        {
            foreach (var format in dataObject.GetFormats(true))
                formatNames.Add(format);
        }
        catch (Exception exception)
        {
            formatsFailure ??= exception;
        }

        if (formatNames.Count == 0 && formatsFailure is not null)
            throw formatsFailure;

        var formats = new List<ClipboardFormatSnapshot>();
        foreach (var format in formatNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!dataObject.GetDataPresent(format, true))
                    continue;
                var value = dataObject.GetData(format, true);
                var (snapshotValue, ownsData) =
                    CloneClipboardValue(value);
                formats.Add(new ClipboardFormatSnapshot(
                    format,
                    snapshotValue,
                    ownsData));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Formats that cannot be materialized are not accessible.
            }
        }

        if (formatNames.Count > 0 && formats.Count == 0)
        {
            throw new InvalidOperationException(
                "No accessible clipboard formats.");
        }

        return formats;
    }

    private static (object? Value, bool OwnsData) CloneClipboardValue(
        object? value)
    {
        if (value is null ||
            value is string ||
            value.GetType().IsValueType)
        {
            return (value, false);
        }

        if (value is Stream stream)
            return (CloneStream(stream), true);

        if (value is BitmapSource bitmap)
        {
            var clone = bitmap.CloneCurrentValue();
            clone.Freeze();
            return (clone, false);
        }

        if (value is Array array)
            return (array.Clone(), false);

        if (value is ICloneable cloneable)
        {
            try
            {
                return (cloneable.Clone(), false);
            }
            catch
            {
                return (value, false);
            }
        }

        return (value, false);
    }

    private static MemoryStream CloneStream(Stream stream)
    {
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = stream.CanSeek &&
                originalPosition <= copy.Length
                ? originalPosition
                : 0;
            return copy;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }

    private static ClipboardTextReadResult WaitForTextOnSta(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var sequenceChanged = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sequence = GetClipboardSequenceNumber();
                if (sequence != baselineSequenceNumber)
                {
                    sequenceChanged = true;
                    var text = ReadUnicodeText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return ClipboardTextReadResult.WithText(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return ClipboardTextReadResult.AccessDenied();
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= timeout)
                return ClipboardTextReadResult.WithoutText(sequenceChanged);

            var remaining = timeout - elapsed;
            var delay = remaining < PollInterval
                ? remaining
                : PollInterval;
            var milliseconds = Math.Max(
                1,
                (int)Math.Ceiling(delay.TotalMilliseconds));
            Thread.Sleep(milliseconds);
        }
    }

    private static string? ReadUnicodeText() =>
        Clipboard.GetText(TextDataFormat.UnicodeText);

    private static void RestoreOnSta(
        ClipboardSessionSnapshot snapshot)
    {
        if (snapshot.Formats.Count == 0)
        {
            Clipboard.Clear();
            return;
        }

        var dataObject = new DataObject();
        foreach (var format in snapshot.Formats)
        {
            try
            {
                dataObject.SetData(
                    format.Format,
                    format.Data,
                    false);
            }
            catch
            {
                // Continue restoring every format that remains accessible.
            }
        }

        if (dataObject.GetFormats(false).Length == 0)
        {
            throw new InvalidOperationException(
                "No clipboard formats could be restored.");
        }

        Clipboard.SetDataObject(dataObject, copy: true);
    }

    private static void DisposeFormats(
        IEnumerable<ClipboardFormatSnapshot>? formats)
    {
        if (formats is null)
            return;
        foreach (var format in formats)
        {
            if (!format.OwnsData ||
                format.Data is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Clipboard payload cleanup is best-effort.
            }
        }
    }

    private sealed class ClipboardStaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _stopped;

        public ClipboardStaWorker()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FloatDesk.Clipboard"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> InvokeAsync<T>(
            Func<T> action,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(
                    () =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            completion.TrySetCanceled(
                                cancellationToken);
                            return;
                        }

                        try
                        {
                            completion.TrySetResult(action());
                        }
                        catch (OperationCanceledException)
                        {
                            completion.TrySetCanceled(
                                cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            completion.TrySetException(exception);
                        }
                    },
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or
                    ObjectDisposedException)
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        "Clipboard worker is unavailable."));
            }

            return completion.Task;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                _queue.CompleteAdding();
        }

        public void Dispose() => Stop();

        private void Run()
        {
            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                    action();
            }
            finally
            {
                _queue.Dispose();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

internal sealed class Win32HotkeyModifierState : IHotkeyModifierState
{
    private static readonly int[] ModifierKeys =
    {
        0x10, // VK_SHIFT
        0x11, // VK_CONTROL
        0x12, // VK_MENU
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
        0xA0, // VK_LSHIFT
        0xA1, // VK_RSHIFT
        0xA2, // VK_LCONTROL
        0xA3, // VK_RCONTROL
        0xA4, // VK_LMENU
        0xA5  // VK_RMENU
    };

    public bool AreAnyModifiersHeld()
    {
        foreach (var key in ModifierKeys)
        {
            if ((NativeInput.GetAsyncKeyState(key) & 0x8000) != 0)
                return true;
        }

        return false;
    }

    private static class NativeInput
    {
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}

internal sealed class Win32CopyShortcutSender : ICopyShortcutSender
{
    private const uint InputKeyboard = 1;
    private const ushort KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;

    public bool TrySendCopy()
    {
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyControl, 0),
            KeyboardInput(VirtualKeyC, 0),
            KeyboardInput(VirtualKeyC, KeyEventKeyUp),
            KeyboardInput(VirtualKeyControl, KeyEventKeyUp)
        };
        var inserted = NativeInput.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeInput.Input>());
        return inserted == (uint)inputs.Length;
    }

    private static NativeInput.Input KeyboardInput(
        ushort virtualKey,
        ushort flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new NativeInput.InputUnion
            {
                Keyboard = new NativeInput.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags
                }
            }
        };

    private static class NativeInput
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal MouseInput Mouse;

            [FieldOffset(0)]
            internal KeyboardInput Keyboard;

            [FieldOffset(0)]
            internal HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal nint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HardwareInput
        {
            internal uint Message;
            internal ushort ParameterLow;
            internal ushort ParameterHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);
    }
}

internal sealed class Win32ForegroundActivator : IForegroundActivator
{
    private static readonly TimeSpan ActivationTimeout =
        TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ActivationPollInterval =
        TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ProcessStartTolerance =
        TimeSpan.FromSeconds(2);

    public ForegroundTargetIdentity? CaptureIdentity(
        ForegroundSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            if (target.TopLevelHwnd == 0 ||
                !NativeForeground.IsWindow(target.TopLevelHwnd))
            {
                return null;
            }

            var threadId = NativeForeground.GetWindowThreadProcessId(
                target.TopLevelHwnd,
                out var processId);
            if (threadId == 0 ||
                processId == 0 ||
                processId != target.ProcessId ||
                (target.ThreadId != 0 &&
                 threadId != target.ThreadId))
            {
                return null;
            }

            var title = ReadWindowTitle(target.TopLevelHwnd);
            DateTimeOffset? processStart = target.ProcessStartUtc;
            string? exePath = target.ExePath;
            try
            {
                using var process = Process.GetProcessById(
                    (int)processId);
                var actualStart =
                    process.StartTime.ToUniversalTime();
                if (processStart is not null &&
                    !StartsMatch(processStart.Value, actualStart))
                {
                    return null;
                }
                processStart ??= actualStart;

                string? actualPath = null;
                try
                {
                    actualPath = process.MainModule?.FileName;
                }
                catch
                {
                    if (exePath is not null)
                        return null;
                }

                if (exePath is not null &&
                    actualPath is not null &&
                    !string.Equals(
                        exePath,
                        actualPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                exePath ??= actualPath;
            }
            catch
            {
                if (processStart is not null || exePath is not null)
                    return null;
            }

            if (!string.Equals(
                    title,
                    ReadWindowTitle(target.TopLevelHwnd),
                    StringComparison.Ordinal))
            {
                return null;
            }

            return new ForegroundTargetIdentity(
                target.TopLevelHwnd,
                processId,
                threadId,
                processStart,
                exePath,
                title);
        }
        catch
        {
            return null;
        }
    }

    public bool IsTargetForeground(
        ForegroundTargetIdentity identity)
    {
        try
        {
            return NativeForeground.GetForegroundWindow() ==
                    identity.Hwnd &&
                IdentityMatches(identity);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryActivateAsync(
        ForegroundTargetIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NativeForeground.GetForegroundWindow() == identity.Hwnd)
            return IdentityMatches(identity);
        if (!IdentityMatches(identity))
            return false;

        try
        {
            if (!NativeForeground.SetForegroundWindow(identity.Hwnd))
                return false;
        }
        catch
        {
            return false;
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < ActivationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeForeground.GetForegroundWindow() ==
                    identity.Hwnd &&
                IdentityMatches(identity))
            {
                return true;
            }

            await Task.Delay(
                    ActivationPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static bool IdentityMatches(
        ForegroundTargetIdentity identity)
    {
        if (identity.Hwnd == 0 ||
            !NativeForeground.IsWindow(identity.Hwnd) ||
            !string.Equals(
                ReadWindowTitle(identity.Hwnd),
                identity.Title,
                StringComparison.Ordinal))
        {
            return false;
        }

        var threadId = NativeForeground.GetWindowThreadProcessId(
            identity.Hwnd,
            out var processId);
        if (threadId == 0 ||
            processId != identity.ProcessId ||
            threadId != identity.ThreadId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (identity.ProcessStartUtc is not null &&
                !StartsMatch(
                    identity.ProcessStartUtc.Value,
                    process.StartTime.ToUniversalTime()))
            {
                return false;
            }

            if (identity.ExePath is not null)
            {
                var path = process.MainModule?.FileName;
                if (!string.Equals(
                        identity.ExePath,
                        path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        catch
        {
            return identity.ProcessStartUtc is null &&
                identity.ExePath is null;
        }

        return true;
    }

    private static bool StartsMatch(
        DateTimeOffset expected,
        DateTimeOffset actual) =>
        (expected - actual).Duration() <= ProcessStartTolerance;

    private static string ReadWindowTitle(nint hwnd)
    {
        var length = NativeForeground.GetWindowTextLength(hwnd);
        var buffer = new char[Math.Max(length + 1, 1)];
        var copied = NativeForeground.GetWindowText(
            hwnd,
            buffer,
            buffer.Length);
        return new string(
            buffer,
            0,
            Math.Clamp(copied, 0, buffer.Length));
    }

    private static class NativeForeground
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hwnd);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint hwnd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            nint hwnd,
            out uint processId);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "GetWindowTextW")]
        internal static extern int GetWindowText(
            nint hwnd,
            [Out] char[] text,
            int maximumCount);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "GetWindowTextLengthW")]
        internal static extern int GetWindowTextLength(nint hwnd);
    }
}
