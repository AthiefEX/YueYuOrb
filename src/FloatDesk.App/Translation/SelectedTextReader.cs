using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

public sealed class SelectedTextReader :
    ISelectedTextReader,
    IDisposable,
    IAsyncDisposable
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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeReadCount;
    private int _disposeState;

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

    internal int ActiveReadCount => Volatile.Read(ref _activeReadCount);

    public async Task<SelectedTextReadResult> ReadAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        Interlocked.Increment(ref _activeReadCount);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            EndRead();
            ThrowDisposed(this);
        }

        CancellationTokenSource? linkedCancellation = null;
        try
        {
            linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token);
            var token = linkedCancellation.Token;
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await ReadCoreAsync(target, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            linkedCancellation?.Dispose();
            EndRead();
        }
    }

    private async Task<SelectedTextReadResult> ReadCoreAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = CaptureTargetIdentity(target);
        if (identity is null)
            return TargetNotForeground();

        if (!await EnsureForegroundAsync(identity, cancellationToken)
                .ConfigureAwait(false))
        {
            return TargetNotForeground();
        }

        if (!await WaitForModifierReleaseAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return ModifiersHeld();
        }

        if (!await EnsureForegroundAsync(identity, cancellationToken)
                .ConfigureAwait(false))
        {
            return TargetNotForeground();
        }

        ClipboardSessionSnapshot? snapshot = null;
        uint? expectedRestoreSequence = null;
        try
        {
            try
            {
                snapshot = await _clipboard.CaptureAsync(cancellationToken)
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

            expectedRestoreSequence = snapshot.SequenceNumber;
            cancellationToken.ThrowIfCancellationRequested();

            if (!await EnsureForegroundAsync(identity, cancellationToken)
                    .ConfigureAwait(false))
            {
                return TargetNotForeground();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!AreModifiersReleased())
                return ModifiersHeld();

            cancellationToken.ThrowIfCancellationRequested();
            if (!await EnsureForegroundAsync(identity, cancellationToken)
                    .ConfigureAwait(false))
            {
                return TargetNotForeground();
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint sequenceBeforeInjection;
            try
            {
                sequenceBeforeInjection =
                    await _clipboard.GetSequenceNumberAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                expectedRestoreSequence = null;
                return ClipboardDenied();
            }

            if (sequenceBeforeInjection != snapshot.SequenceNumber)
            {
                expectedRestoreSequence = null;
                return ClipboardDenied();
            }

            cancellationToken.ThrowIfCancellationRequested();
            CopyShortcutSendResult sendResult;
            try
            {
                sendResult = _sender.SendCopy();
            }
            catch
            {
                return NoSelectedText();
            }

            if (!sendResult.Succeeded)
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

            return await InterpretReadResultAsync(
                    readResult,
                    snapshot.SequenceNumber,
                    sequence => expectedRestoreSequence = sequence)
                .ConfigureAwait(false);
        }
        finally
        {
            if (snapshot is not null)
            {
                await RestoreSnapshotAsync(
                        snapshot,
                        expectedRestoreSequence)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<SelectedTextReadResult> InterpretReadResultAsync(
        ClipboardTextReadResult readResult,
        uint snapshotSequenceNumber,
        Action<uint?> setExpectedRestoreSequence)
    {
        uint currentSequenceNumber;
        try
        {
            currentSequenceNumber =
                await _clipboard.GetSequenceNumberAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch
        {
            setExpectedRestoreSequence(null);
            return ClipboardDenied();
        }

        if (readResult.ObservedSequenceNumber is not uint observedSequence)
        {
            if (currentSequenceNumber != snapshotSequenceNumber)
            {
                setExpectedRestoreSequence(null);
                return ClipboardDenied();
            }

            setExpectedRestoreSequence(snapshotSequenceNumber);
            return readResult.Denied
                ? ClipboardDenied()
                : NoSelectedText();
        }

        if (currentSequenceNumber != observedSequence)
        {
            setExpectedRestoreSequence(null);
            return ClipboardDenied();
        }

        setExpectedRestoreSequence(observedSequence);
        if (readResult.Denied ||
            observedSequence == snapshotSequenceNumber)
        {
            return ClipboardDenied();
        }

        if (string.IsNullOrWhiteSpace(readResult.Text))
            return NoSelectedText();

        return SelectedTextReadResult.Ok(readResult.Text);
    }

    private async Task RestoreSnapshotAsync(
        ClipboardSessionSnapshot snapshot,
        uint? expectedSequenceNumber)
    {
        try
        {
            if (expectedSequenceNumber is uint expectedSequence)
            {
                await _clipboard.RestoreIfSequenceAsync(
                        snapshot,
                        expectedSequence)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Restoration is best-effort and cannot leak clipboard details.
        }
        finally
        {
            try
            {
                snapshot.Dispose();
            }
            catch
            {
                // Snapshot payload cleanup is best-effort.
            }
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

    private async Task<bool> EnsureForegroundAsync(
        ForegroundTargetIdentity identity,
        CancellationToken cancellationToken)
    {
        if (IsForeground(identity))
            return true;

        try
        {
            if (!await _foreground.TryActivateAsync(
                    identity,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return IsForeground(identity);
    }

    private bool AreModifiersReleased()
    {
        try
        {
            return !_modifiers.AreAnyModifiersHeld();
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);
    }

    [DoesNotReturn]
    private static void ThrowDisposed(SelectedTextReader reader)
    {
        ObjectDisposedException.ThrowIf(true, reader);
    }

    private void EndRead()
    {
        if (Interlocked.Decrement(ref _activeReadCount) == 0 &&
            Volatile.Read(ref _disposeState) != 0)
        {
            _drained.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(
                ref _disposeState,
                1,
                0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        _lifetimeCancellation.Cancel();
        if (Volatile.Read(ref _activeReadCount) == 0)
            _drained.TrySetResult();

        try
        {
            await _drained.Task.ConfigureAwait(false);
            await DisposeClipboardSessionAsync().ConfigureAwait(false);
            _operationGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
            _disposeCompletion.TrySetResult();
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeClipboardSessionAsync()
    {
        try
        {
            if (_clipboard is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_clipboard is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // A clipboard adapter cannot block reader disposal.
        }
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
    nint TopLevelHwnd,
    nint FocusHwnd,
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

internal sealed record SendInputEvent(
    ushort VirtualKey,
    ushort Flags);

internal interface ISendInputBackend
{
    int SendInput(IReadOnlyList<SendInputEvent> events);
}

internal readonly record struct CopyShortcutSendResult(
    int RequestedCount,
    int InsertedCount,
    int CompensationRequestedCount,
    int CompensationInsertedCount)
{
    public bool Succeeded =>
        InsertedCount == RequestedCount &&
        CompensationInsertedCount == CompensationRequestedCount;

    public static CopyShortcutSendResult Complete(int requestedCount) =>
        new(requestedCount, requestedCount, 0, 0);
}

internal interface ICopyShortcutSender
{
    CopyShortcutSendResult SendCopy();
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
        ClipboardSnapshotMaterializer.DisposeFormats(Formats);
    }
}

internal readonly record struct ClipboardTextReadResult(
    bool Denied,
    uint? ObservedSequenceNumber,
    string? Text)
{
    public static ClipboardTextReadResult WithText(
        string text,
        uint observedSequenceNumber) =>
        new(false, observedSequenceNumber, text);

    public static ClipboardTextReadResult WithoutText(
        uint? observedSequenceNumber) =>
        new(false, observedSequenceNumber, null);

    public static ClipboardTextReadResult AccessDenied(
        uint? observedSequenceNumber = null) =>
        new(true, observedSequenceNumber, null);
}

internal interface IClipboardSession
{
    Task<ClipboardSessionSnapshot?> CaptureAsync(
        CancellationToken cancellationToken);

    Task<uint> GetSequenceNumberAsync(
        CancellationToken cancellationToken);

    Task<ClipboardTextReadResult> WaitForTextAsync(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<bool> RestoreIfSequenceAsync(
        ClipboardSessionSnapshot snapshot,
        uint expectedSequenceNumber);
}

internal static class ClipboardSnapshotMaterializer
{
    public static IReadOnlyList<ClipboardFormatSnapshot> CaptureFormats(
        IDataObject dataObject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
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
                var (snapshotValue, ownsData) = CloneValue(value);
                formats.Add(new ClipboardFormatSnapshot(
                    format,
                    snapshotValue,
                    ownsData));
            }
            catch (OperationCanceledException)
            {
                DisposeFormats(formats);
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

    public static (object? Value, bool OwnsData) CloneValue(object? value)
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

    public static DataObject CreateDataObject(
        ClipboardSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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

        return dataObject;
    }

    public static void DisposeFormats(
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
                // Snapshot payload cleanup is best-effort.
            }
        }
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
}

internal sealed class WpfClipboardSession :
    IClipboardSession,
    IDisposable,
    IAsyncDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CaptureRetryDelay =
        TimeSpan.FromMilliseconds(15);
    private const int CaptureAttempts = 3;

    private readonly object _sync = new();
    private ClipboardStaWorker? _worker;
    private Task? _disposeTask;
    private int _disposed;

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
                await StopWorkerAsync(worker).ConfigureAwait(false);
            return snapshot;
        }
        catch
        {
            await StopWorkerAsync(worker).ConfigureAwait(false);
            throw;
        }
    }

    public Task<uint> GetSequenceNumberAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(
            NativeClipboard.GetClipboardSequenceNumber());
    }

    public Task<ClipboardTextReadResult> WaitForTextAsync(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var worker = EnsureWorker();
        return worker.InvokeAsync(
            () => WaitForTextOnSta(
                baselineSequenceNumber,
                timeout,
                cancellationToken),
            cancellationToken);
    }

    public async Task<bool> RestoreIfSequenceAsync(
        ClipboardSessionSnapshot snapshot,
        uint expectedSequenceNumber)
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
            return await worker.InvokeAsync(
                    () => RestoreIfSequenceOnSta(
                        snapshot,
                        expectedSequenceNumber),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await worker.StopAndWaitAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            return new ValueTask(
                _disposeTask ??= DisposeCoreAsync());
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        ClipboardStaWorker? worker;
        lock (_sync)
        {
            worker = _worker;
            _worker = null;
        }

        if (worker is not null)
            await worker.StopAndWaitAsync().ConfigureAwait(false);
    }

    private ClipboardStaWorker EnsureWorker()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            return _worker ??= new ClipboardStaWorker();
        }
    }

    private async Task StopWorkerAsync(ClipboardStaWorker worker)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_worker, worker))
                _worker = null;
        }

        await worker.StopAndWaitAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
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
                sequenceBefore =
                    NativeClipboard.GetClipboardSequenceNumber();
            }
            catch
            {
                return null;
            }

            IReadOnlyList<ClipboardFormatSnapshot>? formats = null;
            try
            {
                var dataObject = Clipboard.GetDataObject();
                formats = dataObject is null
                    ? Array.Empty<ClipboardFormatSnapshot>()
                    : ClipboardSnapshotMaterializer.CaptureFormats(
                        dataObject,
                        cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var sequenceAfter =
                    NativeClipboard.GetClipboardSequenceNumber();
                if (sequenceBefore == sequenceAfter)
                {
                    return new ClipboardSessionSnapshot(
                        sequenceAfter,
                        formats);
                }
            }
            catch (OperationCanceledException)
            {
                ClipboardSnapshotMaterializer.DisposeFormats(formats);
                throw;
            }
            catch
            {
                ClipboardSnapshotMaterializer.DisposeFormats(formats);
                return null;
            }

            ClipboardSnapshotMaterializer.DisposeFormats(formats);
            if (attempt + 1 < CaptureAttempts)
                Thread.Sleep(CaptureRetryDelay);
        }

        return null;
    }

    private static ClipboardTextReadResult WaitForTextOnSta(
        uint baselineSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        uint? observedSequenceNumber = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sequence =
                    NativeClipboard.GetClipboardSequenceNumber();
                if (sequence != baselineSequenceNumber)
                {
                    observedSequenceNumber = sequence;
                    var text = ReadUnicodeText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return ClipboardTextReadResult.WithText(
                            text,
                            sequence);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return ClipboardTextReadResult.AccessDenied(
                    observedSequenceNumber);
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= timeout)
            {
                return ClipboardTextReadResult.WithoutText(
                    observedSequenceNumber);
            }

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

    private static bool RestoreIfSequenceOnSta(
        ClipboardSessionSnapshot snapshot,
        uint expectedSequenceNumber)
    {
        if (NativeClipboard.GetClipboardSequenceNumber() !=
            expectedSequenceNumber)
        {
            return false;
        }

        if (snapshot.Formats.Count == 0)
        {
            Clipboard.Clear();
            return true;
        }

        var dataObject =
            ClipboardSnapshotMaterializer.CreateDataObject(snapshot);
        Clipboard.SetDataObject(dataObject, copy: true);
        return true;
    }

    private static string? ReadUnicodeText() =>
        Clipboard.GetText(TextDataFormat.UnicodeText);

    private sealed class ClipboardStaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
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

        public Task StopAndWaitAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                _queue.CompleteAdding();
            return _exit.Task;
        }

        public void Dispose()
        {
            StopAndWaitAsync().GetAwaiter().GetResult();
        }

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
                _exit.TrySetResult();
            }
        }
    }
}

internal static class NativeClipboard
{
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
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
    private const ushort KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;

    private static readonly SendInputEvent[] CopyEvents =
    {
        new(VirtualKeyControl, 0),
        new(VirtualKeyC, 0),
        new(VirtualKeyC, KeyEventKeyUp),
        new(VirtualKeyControl, KeyEventKeyUp)
    };

    private static readonly SendInputEvent[] CompensationEvents =
    {
        new(VirtualKeyC, KeyEventKeyUp),
        new(VirtualKeyControl, KeyEventKeyUp)
    };

    private readonly ISendInputBackend _backend;

    public Win32CopyShortcutSender()
        : this(new Win32SendInputBackend())
    {
    }

    internal Win32CopyShortcutSender(ISendInputBackend backend)
    {
        _backend = backend ??
            throw new ArgumentNullException(nameof(backend));
    }

    public CopyShortcutSendResult SendCopy()
    {
        var insertedCount = Math.Clamp(
            _backend.SendInput(CopyEvents),
            0,
            CopyEvents.Length);
        if (insertedCount == CopyEvents.Length)
            return CopyShortcutSendResult.Complete(CopyEvents.Length);

        if (insertedCount == 0)
        {
            return new CopyShortcutSendResult(
                CopyEvents.Length,
                insertedCount,
                0,
                0);
        }

        var compensationInsertedCount = Math.Clamp(
            _backend.SendInput(CompensationEvents),
            0,
            CompensationEvents.Length);
        return new CopyShortcutSendResult(
            CopyEvents.Length,
            insertedCount,
            CompensationEvents.Length,
            compensationInsertedCount);
    }
}

internal sealed class Win32SendInputBackend : ISendInputBackend
{
    private const uint InputKeyboard = 1;

    public int SendInput(IReadOnlyList<SendInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return 0;

        var inputs = new NativeInput.Input[events.Count];
        for (var index = 0; index < events.Count; index++)
        {
            var inputEvent = events[index];
            inputs[index] = new NativeInput.Input
            {
                Type = InputKeyboard,
                Union = new NativeInput.InputUnion
                {
                    Keyboard = new NativeInput.KeyboardInput
                    {
                        VirtualKey = inputEvent.VirtualKey,
                        Flags = inputEvent.Flags
                    }
                }
            };
        }

        var inserted = NativeInput.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeInput.Input>());
        return inserted > int.MaxValue
            ? int.MaxValue
            : (int)inserted;
    }

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
    private const uint GetAncestorRoot = 2;
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
                target.FocusHwnd == 0 ||
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

            var focusHwnd = ReadFocusHwnd(threadId);
            if (focusHwnd == 0 ||
                focusHwnd != target.FocusHwnd ||
                NativeForeground.GetAncestor(
                    focusHwnd,
                    GetAncestorRoot) != target.TopLevelHwnd)
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
                focusHwnd,
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
                    identity.TopLevelHwnd &&
                FocusMatches(identity) &&
                TopLevelMatches(identity);
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
        if (!TopLevelMatches(identity))
            return false;

        if (NativeForeground.GetForegroundWindow() ==
                identity.TopLevelHwnd &&
            FocusMatches(identity))
        {
            return true;
        }

        try
        {
            if (!NativeForeground.SetForegroundWindow(
                    identity.TopLevelHwnd))
            {
                return false;
            }
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
                    identity.TopLevelHwnd &&
                FocusMatches(identity) &&
                TopLevelMatches(identity))
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

    private static bool FocusMatches(
        ForegroundTargetIdentity identity)
    {
        var focusHwnd = ReadFocusHwnd(identity.ThreadId);
        return focusHwnd != 0 &&
            focusHwnd == identity.FocusHwnd &&
            NativeForeground.GetAncestor(
                focusHwnd,
                GetAncestorRoot) == identity.TopLevelHwnd;
    }

    private static bool TopLevelMatches(
        ForegroundTargetIdentity identity)
    {
        if (identity.TopLevelHwnd == 0 ||
            !NativeForeground.IsWindow(identity.TopLevelHwnd) ||
            !string.Equals(
                ReadWindowTitle(identity.TopLevelHwnd),
                identity.Title,
                StringComparison.Ordinal))
        {
            return false;
        }

        var threadId = NativeForeground.GetWindowThreadProcessId(
            identity.TopLevelHwnd,
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

    private static nint ReadFocusHwnd(uint threadId)
    {
        if (threadId == 0)
            return 0;
        var info = new NativeForeground.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeForeground.GuiThreadInfo>()
        };
        return NativeForeground.GetGUIThreadInfo(threadId, ref info)
            ? info.FocusWindow
            : 0;
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
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GuiThreadInfo
        {
            internal int Size;
            internal uint Flags;
            internal nint ActiveWindow;
            internal nint FocusWindow;
            internal nint CaptureWindow;
            internal nint MenuOwnerWindow;
            internal nint MoveSizeWindow;
            internal nint CaretWindow;
            internal NativeRect CaretRect;
        }

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(
            uint threadId,
            ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(
            nint hwnd,
            uint flags);

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
