using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Danmaku;

public interface IDanmakuSurface
{
    bool CanPresent { get; }
    void ApplySettings(DanmakuSettings settings);
    void Present(DanmakuMessage message, bool highlighted);
    void Clear();
}

/// <summary>Owned by the UI dispatcher; notifications never survive a pause or settings change.</summary>
public sealed class DanmakuRuntime(IDanmakuNotificationSource source, IDanmakuSurface surface) : IDisposable
{
    private DanmakuSettings _settings = new();
    private readonly DanmakuBuffer _messages = new();
    private CancellationTokenSource _operation = new();
    private int _generation, _captureDepth;
    private bool _polling, _locked, _paused, _disposed, _subtitleActive;
    public string Status => !_settings.Enabled ? "消息弹幕已关闭" : _paused ? "弹幕已暂停" : _locked || _captureDepth > 0 ? "弹幕暂时隐藏" : source.Status;
    private bool Active => !_disposed && _settings.Enabled && !_paused && !_locked && !_subtitleActive && _captureDepth == 0;
    public void ApplySettings(DanmakuSettings settings)
    {
        settings = settings.Normalize();
        if (_settings == settings) return;
        _settings = settings; Invalidate(); surface.ApplySettings(settings);
    }
    public void SetPaused(bool paused) { if (_paused == paused) return; _paused = paused; Invalidate(); }
    public void SetSubtitleSessionActive(bool active) { if (_subtitleActive == active) return; _subtitleActive = active; Invalidate(); }
    public void SetSessionLocked(bool locked) { if (_locked == locked) return; _locked = locked; Invalidate(); }
    public IDisposable SuspendForCapture()
    {
        _captureDepth++; Invalidate();
        return new Lease(() => { if (_disposed) return; _captureDepth--; Invalidate(); });
    }
    public void Clear() => Invalidate();
    private void Invalidate()
    {
        _generation++; _operation.Cancel(); _operation.Dispose(); _operation = new();
        _messages.Clear(); source.Reset(); surface.Clear();
    }
    public async Task PollAsync()
    {
        if (!Active || _polling) return;
        _polling = true;
        var version = _generation;
        try
        {
            var incoming = await source.PollAsync(_operation.Token);
            if (version != _generation || !Active) return;
            if (!source.HasAccess) { Invalidate(); return; }
            foreach (var message in incoming)
                if (DanmakuBuffer.Matches(message, _settings)) _messages.Enqueue(message, DateTimeOffset.UtcNow);
            if (surface.CanPresent && _messages.Take(DateTimeOffset.UtcNow) is { } next)
                surface.Present(next, DanmakuBuffer.IsHighlighted(next, _settings));
        }
        catch (OperationCanceledException) { }
        finally { _polling = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Invalidate(); _operation.Dispose();
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
