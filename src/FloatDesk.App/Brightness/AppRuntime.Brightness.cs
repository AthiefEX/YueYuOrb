using FloatDesk.App.Brightness;
using FloatDesk.App.Views;
using FloatDesk.Core.Brightness;
using FloatDesk.Core.Models;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private readonly FloatDesk.Windows.Brightness.WindowsBrightnessService _brightness = new();
    private NightModeController? _nightMode;
    private BrightnessControlWindow? _brightnessWindow;
    private bool _brightnessBusy;
    private (int Value, bool Relative)? _nextBrightness;

    private void InitializeBrightness()
    {
        _nightMode = new(System.Windows.Application.Current.Dispatcher);
        _nightMode.SetDimmingPercent(_settings.Current.Brightness.NightDimmingPercent);
        _nightMode.Changed += () => _panel?.SetNightModeEnabled(_nightMode.IsEnabled);
        _coordinator!.CapturePauseFactory = () => _nightMode.Suspend();
        _translationRuntime.SubtitleStateChanged += OnNightSubtitleChanged;
        _coordinator.WindowCreated += window => window.IsVisibleChanged += (_, _) => _nightMode.RaiseControls();
        _coordinator.WindowCreated += AttachNightPreview;
        _coordinator.ToolShown += _ => _nightMode.RaiseControls();
        if (_orb is not null) _orb.IsVisibleChanged += (_, _) => _nightMode.RaiseControls();
        if (_panel is not null) _panel.IsVisibleChanged += (_, _) =>
        {
            _panel.SetNightModeEnabled(_nightMode.IsEnabled);
            _nightMode.RaiseControls();
        };
    }
    private void AttachNightPreview(System.Windows.Window window)
    {
        if (window is not SettingsWindow) return;
        FloatDesk.App.ViewModels.SettingsViewModel? previewModel = null;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(FloatDesk.App.ViewModels.SettingsViewModel.NightDimming) && previewModel is not null)
                _nightMode?.SetDimmingPercent(previewModel.NightDimming);
        };
        void Bind()
        {
            if (previewModel is not null) previewModel.PropertyChanged -= handler;
            previewModel = window.DataContext as FloatDesk.App.ViewModels.SettingsViewModel;
            if (previewModel is not null) previewModel.PropertyChanged += handler;
        }
        Bind();
        window.DataContextChanged += (_, _) => Bind();
        window.Closed += (_, _) =>
        {
            if (previewModel is not null) previewModel.PropertyChanged -= handler;
            _nightMode?.SetDimmingPercent(_settings.Current.Brightness.NightDimmingPercent);
        };
    }
    private void OnNightSubtitleChanged() => System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        _nightMode?.SetSubtitleSessionActive(_translationRuntime.IsSubtitleRunning);
    });
    private void ToggleNightMode()
    {
        _nightMode?.SetSubtitleSessionActive(_translationRuntime.IsSubtitleRunning);
        _nightMode?.SetEnabled(!_nightMode.IsEnabled);
        if (_translationRuntime.IsSubtitleRunning) ShowBrightnessMessage("字幕翻译期间暂时暂停黑夜降暗，停止字幕后恢复。");
    }
    private void RecoverBrightness()
    {
        _nightMode?.SetEnabled(false);
        _ = ChangeBrightnessAsync(50, false);
    }
    private void OpenBrightnessControls()
    {
        if (_popupShutdownInProgress) return;
        if (_nightMode is null) return;
        if (_brightnessWindow is { IsVisible: true }) { _brightnessWindow.Activate(); return; }
        var window = new BrightnessControlWindow(_brightness, _nightMode, _settings.Current.Brightness);
        RememberPopupPosition(window, "brightness-panel");
        _brightnessWindow = window;
        _coordinator?.RegisterCaptureHiddenWindow(window);
        window.Closed += (_, _) =>
        {
            _coordinator?.UnregisterCaptureHiddenWindow(window);
            if (ReferenceEquals(_brightnessWindow, window)) _brightnessWindow = null;
        };
        window.Show();
    }
    private bool HandleBrightnessHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.BrightnessPanel: OpenBrightnessControls(); break;
            case HotkeyAction.BrightnessPreset: _ = ChangeBrightnessAsync(_settings.Current.Brightness.TargetPercent, false); break;
            case HotkeyAction.BrightnessUp: _ = ChangeBrightnessAsync(_settings.Current.Brightness.StepPercent, true); break;
            case HotkeyAction.BrightnessDown: _ = ChangeBrightnessAsync(-_settings.Current.Brightness.StepPercent, true); break;
            case HotkeyAction.ToggleNightMode: ToggleNightMode(); break;
            case HotkeyAction.RecoverBrightness: RecoverBrightness(); break;
            default: return false;
        }
        return true;
    }
    private async Task ChangeBrightnessAsync(int value, bool relative)
    {
        if (_brightnessBusy) { _nextBrightness = (value, relative); return; }
        _brightnessBusy = true;
        try
        {
            var snapshot = await _brightness.GetSnapshotAsync(_lifetime.Token);
            var targets = BrightnessPolicy.CreateTargets(snapshot, value, relative);
            var results = await Task.WhenAll(targets.Select(p => _brightness.SetPercentAsync(p.Key, p.Value, _lifetime.Token)));
            var failures = results.Count(r => r.Error is not null);
            var skipped = snapshot.Devices.Count - targets.Count;
            ShowBrightnessMessage(targets.Count == 0 ? "没有可调节的屏幕，黑夜模式仍可使用。" :
                $"已调节 {results.Length - failures} 个屏幕，失败 {failures} 个，跳过 {skipped} 个。");
        }
        catch (OperationCanceledException) { }
        catch { ShowBrightnessMessage("无法调节亮度，请检查显示器连接和 DDC/CI 设置。"); }
        finally
        {
            _brightnessBusy = false;
            if (_nextBrightness is { } next && !_lifetime.IsCancellationRequested)
            { _nextBrightness = null; _ = ChangeBrightnessAsync(next.Value, next.Relative); }
        }
    }
    private void ShowBrightnessMessage(string text) => _tray?.ShowWarning("亮度调节", text);
}
