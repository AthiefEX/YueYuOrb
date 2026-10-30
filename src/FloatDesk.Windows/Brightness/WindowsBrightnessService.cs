using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Brightness;

public interface IBrightnessBackend
{
    IReadOnlyList<BrightnessDevice> Enumerate();
    BrightnessWriteResult SetPercent(string id, int percent);
}

public sealed class WindowsBrightnessService : IBrightnessService
{
    private sealed record Work(int Percent, int Generation, TaskCompletionSource<BrightnessWriteResult> Completion, CancellationToken Cancellation);
    private sealed class Slot { public Work? Pending; public bool Running; }
    private readonly IBrightnessBackend _backend;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _writes = [];
    private Task<BrightnessSnapshot>? _reading;
    private int _readingGeneration;
    private int _generation;
    private bool _disposed;
    private readonly bool _trackDisplays;

    public WindowsBrightnessService() : this(new BrightnessDeviceDiscovery())
    {
        _trackDisplays = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }
    public WindowsBrightnessService(IBrightnessBackend backend) => _backend = backend;

    public async Task<BrightnessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<BrightnessSnapshot> reading;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
            if (_reading is null || _reading.IsCompleted)
            {
                _readingGeneration = generation;
                _reading = Task.Run(() => new BrightnessSnapshot(_backend.Enumerate()));
            }
            if (_readingGeneration != generation) throw new InvalidOperationException("显示器配置已变化，正在等待旧读取结束。");
            reading = _reading;
        }
        var snapshot = await reading.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _generation) throw new InvalidOperationException("显示器配置已变化，请重试。");
        }
        return snapshot;
    }

    public async Task<BrightnessWriteResult> SetPercentAsync(string deviceId, int percent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<BrightnessWriteResult> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_writes.TryGetValue(deviceId, out var slot)) _writes[deviceId] = slot = new();
            slot.Pending?.Completion.TrySetResult(new(deviceId, null, "已被新的亮度请求替代。"));
            var work = new Work(Math.Clamp(percent, 0, 100), _generation, new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            slot.Pending = work;
            task = work.Completion.Task;
            if (!slot.Running) { slot.Running = true; _ = Task.Run(() => RunDevice(deviceId, slot), CancellationToken.None); }
        }
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(deviceId, null, "显示器响应超时，保留最新请求等待设备恢复。"); }
    }
    private void RunDevice(string id, Slot slot)
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (slot.Pending is not { } pending) { slot.Running = false; return; }
                work = pending; slot.Pending = null;
                if (_disposed || work.Generation != _generation)
                { work.Completion.TrySetResult(new(id, null, "显示器配置已变化或服务已关闭。")); continue; }
            }
            if (work.Cancellation.IsCancellationRequested) { work.Completion.TrySetCanceled(work.Cancellation); continue; }
            BrightnessWriteResult result;
            try { result = _backend.SetPercent(id, work.Percent); }
            catch { result = new(id, null, "显示器亮度操作失败。"); }
            lock (_gate)
                if (_disposed || work.Generation != _generation) result = new(id, null, "显示器配置已变化，旧结果已丢弃。");
            work.Completion.TrySetResult(result);
        }
    }
    private void OnDisplaysChanged(object? sender, EventArgs args) => NotifyTopologyChanged();
    public void NotifyTopologyChanged()
    {
        lock (_gate)
        {
            _generation++;
            if (_backend is BrightnessDeviceDiscovery native) native.Invalidate();
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var (id, slot) in _writes)
            { slot.Pending?.Completion.TrySetResult(new(id, null, "亮度服务已关闭。")); slot.Pending = null; }
        }
        if (_trackDisplays) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
    }
}
