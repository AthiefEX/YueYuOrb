using System.Windows.Threading;
using FloatDesk.App.Danmaku;
using FloatDesk.App.Views;
using Microsoft.Win32;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private DanmakuOverlayWindow? _danmakuWindow;
    private DanmakuRuntime? _danmaku;
    private DispatcherTimer? _danmakuTimer;
    private bool _danmakuPaused;
    private bool HandleDanmakuHotkey(FloatDesk.Core.Models.HotkeyAction action)
    {
        switch (action)
        {
            case FloatDesk.Core.Models.HotkeyAction.ToggleDanmaku: ToggleDanmaku(); break;
            case FloatDesk.Core.Models.HotkeyAction.PauseDanmaku:
                _danmakuPaused = !_danmakuPaused; _danmaku?.SetPaused(_danmakuPaused || _emergencyPaused);
                _tray?.ShowWarning("消息弹幕", _danmakuPaused ? "弹幕已暂停" : "弹幕已恢复，仅接收新通知"); break;
            case FloatDesk.Core.Models.HotkeyAction.ClearDanmaku: _danmaku?.Clear(); break;
            default: return false;
        }
        return true;
    }
    private void InitializeDanmaku()
    {
        _danmakuWindow = new();
        _danmaku = new(new WindowsNotificationSource(), _danmakuWindow);
        _danmaku.ApplySettings(_settings.Current.Danmaku);
        DanmakuLiveStatus.PlayTest = _danmaku.PlayTestMessage;
        _coordinator!.RegisterCaptureHiddenWindow(_danmakuWindow);
        _coordinator.CapturePauseFactory = () => new CaptureVisibilityLease(_nightMode!.Suspend(), _danmaku.SuspendForCapture());
        _panel!.DanmakuRequested += ToggleDanmaku;
        _danmakuTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) =>
        {
            var runtime = _danmaku;
            if (runtime is null) return;
            try { await runtime.PollAsync(); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { runtime.Clear(); }
            DanmakuLiveStatus.Text = _danmaku?.Status ?? "弹幕已停止";
        }, System.Windows.Application.Current.Dispatcher);
        _danmakuTimer.Start();
        SystemEvents.SessionSwitch += OnDanmakuSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDanmakuDisplaysChanged;
        _translationRuntime.SubtitleStateChanged += OnDanmakuSubtitleChanged;
    }
    private async void ToggleDanmaku()
    {
        try
        {
            var result = await _settings.UpdateAsync(current => current with { Danmaku = current.Danmaku with { Enabled = !current.Danmaku.Enabled } }, _lifetime.Token);
            if (!result.Success) _tray?.ShowWarning("消息弹幕", "无法保存弹幕开关，请重试。");
            else if (_settings.Current.Danmaku.Enabled) _tray?.ShowWarning("消息弹幕", _danmaku?.Status ?? "请在设置中授权通知访问。");
        }
        catch (OperationCanceledException) { }
    }
    private void OnDanmakuSessionSwitch(object sender, SessionSwitchEventArgs args) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (args.Reason == SessionSwitchReason.SessionLock) _danmaku?.SetSessionLocked(true);
        if (args.Reason == SessionSwitchReason.SessionUnlock) _danmaku?.SetSessionLocked(false);
    });
    private void OnDanmakuDisplaysChanged(object? sender, EventArgs args) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        _danmakuWindow?.ApplySettings(_settings.Current.Danmaku);
    });
    private void DisposeDanmaku()
    {
        _danmakuTimer?.Stop();
        DanmakuLiveStatus.PlayTest = null;
        DanmakuLiveStatus.Text = "弹幕已停止";
        _translationRuntime.SubtitleStateChanged -= OnDanmakuSubtitleChanged;
        SystemEvents.SessionSwitch -= OnDanmakuSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDanmakuDisplaysChanged;
        _danmaku?.Dispose(); _danmaku = null;
        _danmakuWindow?.Clear(); _danmakuWindow?.Close(); _danmakuWindow = null;
    }
    private void OnDanmakuSubtitleChanged() => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        _danmaku?.SetSubtitleSessionActive(_translationRuntime.IsSubtitleRunning));
    private sealed class CaptureVisibilityLease(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose() { second.Dispose(); first.Dispose(); }
    }
}
