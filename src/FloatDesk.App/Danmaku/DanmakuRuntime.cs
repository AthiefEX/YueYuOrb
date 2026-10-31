using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Danmaku;

public interface IDanmakuSurface
{
    bool CanPresent { get; }
    void ApplySettings(DanmakuSettings settings);
    void Present(DanmakuMessage message, bool highlighted);
    void Clear();
    void RefreshZOrder() { }
}

/// <summary>Owned by the UI dispatcher; notifications never survive a pause or settings change.</summary>
public sealed class DanmakuRuntime(IDanmakuNotificationSource source, IDanmakuSurface surface) : IDisposable
{
    private DanmakuSettings _settings = new();
    private readonly DanmakuBuffer _messages = new();
    private CancellationTokenSource _operation = new();
    private int _generation, _captureDepth;
    private bool _polling, _locked, _paused, _disposed, _subtitleActive;
    private bool _fullscreen;
    private bool _configured;
    private int _displayGeneration;
    private nint _foreground;
    private string? _fullscreenMonitor;
    private bool DisplayAllowed => _settings.DisplayMode switch
    { DanmakuDisplayMode.FullscreenOnly => _fullscreen, DanmakuDisplayMode.WindowedOnly => !_fullscreen, _ => true };
    private DanmakuSettings EffectiveSettings => _fullscreen && _settings.FollowFullscreenMonitor && _fullscreenMonitor is not null
        ? _settings with { MonitorId = _fullscreenMonitor } : _settings;
    public string Status => !_settings.Enabled ? "系统通知弹幕已关闭" : _paused ? "弹幕已暂停" : _subtitleActive ? "字幕翻译期间暂停通知弹幕" : _locked || _captureDepth > 0 ? "弹幕暂时隐藏" :
        (!_settings.ReceiveAllApplications && _settings.SelectedApplications.Count == 0 ? "尚未选择来源应用。请勾选应用或开启接收所有应用。 " : "") +
        (!DisplayAllowed ? "当前显示时机不匹配，通知不会积压。 " : "") + source.Status;
    public bool PlayTestMessage()
    {
        if (_disposed || _locked || _captureDepth > 0 || _subtitleActive || _paused || !surface.CanPresent) return false;
        surface.Present(new("local-test", "月语", "测试弹幕", "这是一条本地测试弹幕，不代表已接收到聊天消息。"), true);
        return true;
    }
    private bool Active => !_disposed && _settings.Enabled && !_paused && !_locked && !_subtitleActive && _captureDepth == 0;
    public void ApplySettings(DanmakuSettings settings)
    {
        settings = settings.Normalize();
        if (_configured && _settings == settings) return;
        _configured = true; _settings = settings; source.ApplySettings(settings); Invalidate(); surface.ApplySettings(EffectiveSettings);
    }
    public void SetDisplayContext(bool fullscreen, nint foreground, string? monitorId)
    {
        if (_disposed || (_fullscreen == fullscreen && _foreground == foreground && _fullscreenMonitor == monitorId)) return;
        var oldMonitor = EffectiveSettings.MonitorId;
        var wasAllowed = DisplayAllowed;
        _fullscreen = fullscreen; _foreground = foreground; _fullscreenMonitor = monitorId;
        if (wasAllowed != DisplayAllowed) _displayGeneration++;
        if (!string.Equals(oldMonitor, EffectiveSettings.MonitorId, StringComparison.OrdinalIgnoreCase)) surface.ApplySettings(EffectiveSettings);
        if (!DisplayAllowed) { _messages.Clear(); surface.Clear(); }
        else surface.RefreshZOrder();
    }
    public void RefreshDisplayPlacement() => surface.ApplySettings(EffectiveSettings);
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
        var displayVersion = _displayGeneration;
        try
        {
            var incoming = await source.PollAsync(_operation.Token);
            if (version != _generation || !Active || displayVersion != _displayGeneration) return;
            if (!source.HasAccess) { Invalidate(); return; }
            if (!DisplayAllowed) { _messages.Clear(); return; }
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
