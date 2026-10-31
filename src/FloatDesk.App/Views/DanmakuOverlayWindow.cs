using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using FloatDesk.App.Danmaku;
using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public sealed class DanmakuOverlayWindow : Window, IDanmakuSurface
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Grid _root = new();
    private readonly Border _editor;
    private DanmakuSettings _settings = new();
    private bool _editing;
    public event Action<DanmakuSettings>? RegionEdited;
    private int Rows => Math.Max(1, (int)((Height - (_editing ? 38 : 0)) / (_settings.FontSize * 1.8)));
    public bool CanPresent => !_editing && (_settings.Vertical ? _canvas.Children.Count < Rows : FreeLane() >= 0);
    private int FreeLane() => Enumerable.Range(0, Rows).FirstOrDefault(i => !_canvas.Children.OfType<FrameworkElement>().Any(c => c.Tag is int lane && lane == i), -1);
    public DanmakuOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        MinWidth = 120; MinHeight = 80;
        _root.Children.Add(_canvas);
        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromArgb(230, 24, 43, 61)), Height = 36, VerticalAlignment = VerticalAlignment.Top };
        var done = new Button { Content = "完成", Margin = new Thickness(4), Padding = new Thickness(8, 0, 8, 0) };
        done.Click += (_, _) => { RegionEdited?.Invoke(CaptureRegion()); Close(); };
        DockPanel.SetDock(done, Dock.Right); bar.Children.Add(done);
        var hint = new TextBlock { Text = "拖动标题移动 · 右下角缩放", Foreground = Brushes.White, Margin = new Thickness(8), Cursor = System.Windows.Input.Cursors.SizeAll };
        hint.MouseLeftButtonDown += (_, args) => { if (args.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        bar.Children.Add(hint);
        _editor = new Border { BorderBrush = Brushes.LightSkyBlue, BorderThickness = new Thickness(1), Child = bar, Visibility = Visibility.Collapsed };
        _root.Children.Add(_editor); Content = _root;
        IsHitTestVisible = false; Focusable = false;
        SourceInitialized += (_, _) =>
        {
            UpdateMouseStyle();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(FilterPassiveMouse);
        };
    }
    private nint FilterPassiveMouse(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (_editing) return 0;
        if (message == 0x84) { handled = true; return new nint(-1); }
        if (message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }
    private void UpdateMouseStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, -20);
        _ = SetWindowLong(handle, -20, _editing ? (style | 0x80) & ~(0x20 | 0x08000000) : style | 0x20 | 0x08000000 | 0x80);
    }
    public void ApplySettings(DanmakuSettings settings)
    {
        _settings = settings.Normalize();
        Clear(); Width = _settings.WidthDip; Height = _settings.HeightDip; Opacity = _settings.Opacity;
        var monitor = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _settings.MonitorId) ?? System.Windows.Forms.Screen.PrimaryScreen!;
        var area = monitor.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Move first so per-monitor DPI is established before converting requested DIPs.
        SetWindowPos(handle, new nint(-1), area.X, area.Y, 0, 0, 0x11);
        var scale = GetDpiForWindow(handle) / 96d;
        var width = Math.Min(area.Width, (int)Math.Round(Width * scale));
        var height = Math.Min(area.Height, (int)Math.Round(Height * scale));
        Width = width / scale; Height = height / scale;
        SetWindowPos(handle, new nint(-1), area.X + (int)((area.Width - width) * _settings.RelativeX), area.Y + (int)((area.Height - height) * _settings.RelativeY), width, height, 0x10);
    }
    public void SetEditing(bool editing)
    {
        _editing = editing; _editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = editing; Focusable = editing;
        ResizeMode = editing ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        Background = editing ? new SolidColorBrush(Color.FromArgb(55, 24, 43, 61)) : Brushes.Transparent;
        UpdateMouseStyle();
        if (editing) Show();
    }
    public DanmakuSettings CaptureRegion()
    {
        var handle = new WindowInteropHelper(this).Handle;
        GetWindowRect(handle, out var rect);
        var monitor = System.Windows.Forms.Screen.FromHandle(handle); var area = monitor.WorkingArea;
        return (_settings with { MonitorId = monitor.DeviceName, WidthDip = ActualWidth, HeightDip = ActualHeight,
            RelativeX = (rect.Left - area.Left) / (double)Math.Max(1, area.Width - (rect.Right - rect.Left)),
            RelativeY = (rect.Top - area.Top) / (double)Math.Max(1, area.Height - (rect.Bottom - rect.Top)) }).Normalize();
    }
    public void Present(DanmakuMessage message, bool highlighted)
    {
        if (!_editing && !CanPresent) return;
        var content = (_settings.ShowTitle ? $"[{message.Platform} · {message.Title}] " : "") + message.Text;
        var text = new TextBlock { Text = content[..Math.Min(content.Length, 512)], FontSize = _settings.FontSize,
            FontFamily = new FontFamily(_settings.FontFamily), FontWeight = highlighted ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(highlighted ? _settings.HighlightColor : _settings.Color)),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = .9 },
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = _settings.Vertical || _settings.ReduceMotion ? Width : 2048 };
        var rowHeight = _settings.FontSize * 1.8;
        var lane = Math.Max(0, FreeLane()); text.Tag = lane;
        if (_settings.Vertical)
        {
            foreach (FrameworkElement old in _canvas.Children)
            {
                var target = Canvas.GetTop(old) - rowHeight;
                Canvas.SetTop(old, target);
                if (!_settings.ReduceMotion && old.RenderTransform is TranslateTransform shift)
                    shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rowHeight, 0, TimeSpan.FromMilliseconds(180)));
            }
            Canvas.SetTop(text, Math.Max(_editing ? 40 : 0, Height - rowHeight));
        }
        else Canvas.SetTop(text, (_editing ? 40 : 0) + lane * rowHeight);
        _canvas.Children.Add(text); text.Measure(new Size(2048, rowHeight));
        var transform = new TranslateTransform(); text.RenderTransform = transform;
        var duration = TimeSpan.FromSeconds(_settings.DurationSeconds);
        if (!_settings.Vertical && !_settings.ReduceMotion)
        {
            var animation = new DoubleAnimation(Width, -text.DesiredSize.Width - 6, duration);
            animation.Completed += (_, _) => Remove(text);
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            if (_settings.Vertical && !_settings.ReduceMotion)
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rowHeight, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var life = new DoubleAnimation(1, 1, duration);
            life.Completed += (_, _) => Remove(text);
            text.BeginAnimation(OpacityProperty, life);
        }
        if (!IsVisible) Show();
    }
    private void Remove(FrameworkElement text) { _canvas.Children.Remove(text); if (_canvas.Children.Count == 0 && !_editing) Hide(); }
    public void Clear()
    {
        foreach (FrameworkElement item in _canvas.Children)
        {
            item.BeginAnimation(OpacityProperty, null);
            if (item.RenderTransform is TranslateTransform move) { move.BeginAnimation(TranslateTransform.XProperty, null); move.BeginAnimation(TranslateTransform.YProperty, null); }
        }
        _canvas.Children.Clear(); if (!_editing && IsVisible) Hide();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
