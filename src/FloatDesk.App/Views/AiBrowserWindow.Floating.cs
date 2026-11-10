using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class AiBrowserWindow
{
    private FloatingBrowserSettings _floating = new();
    private bool _floatingApplied;
    private bool _applyingFloating;
    private nint _previousForeground;
    private string _interactionHotkey = "Ctrl+Alt+B";
    private double _windowOpacity = 1;
    public double DisplayOpacity => _windowOpacity;
    private readonly DispatcherTimer _floatingSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Func<Func<FloatingBrowserSettings, FloatingBrowserSettings>, Task>? _persistFloating;
    private Func<string, Uri, Task>? _addBookmark;
    public bool IsClickThrough { get; private set; } = true;

    public void ConfigureFloating(Func<Func<FloatingBrowserSettings, FloatingBrowserSettings>, Task> persist, Func<string, Uri, Task> bookmark)
    { _persistFloating = persist; _addBookmark = bookmark; }

    private void InitializeFloatingBrowser()
    {
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(FloatingHook);
            UpdateInteractionStyles();
            ApplyWindowOpacity();
        };
        LocationChanged += (_, _) => ScheduleFloatingSave();
        SizeChanged += (_, _) =>
        {
            BrowserCaption.Visibility = ActualWidth >= 560 ? Visibility.Visible : Visibility.Collapsed;
            ScheduleFloatingSave();
        };
        Closed += (_, _) => _floatingSave.Stop();
        _floatingSave.Tick += async (_, _) =>
        {
            _floatingSave.Stop();
            if (_persistFloating is null || !_floatingApplied || WindowState != WindowState.Normal) return;
            var bounds = new BrowserWindowBounds(Left, Top, ActualWidth, ActualHeight);
            var opacity = _windowOpacity;
            try { await _persistFloating(current => current with { Bounds = bounds, Opacity = opacity }); }
            catch (IOException) { FooterText.Text = "窗口设置未保存，请重试。"; }
        };
        SetInteraction(false);
    }

    private void ApplyFloatingSettings(FloatingBrowserSettings settings)
    {
        _applyingFloating = true;
        try
        {
            _floating = settings;
            _windowOpacity = Math.Clamp(settings.Opacity, 0.2, 1);
            ApplyWindowOpacity();
            BrowserOpacitySlider.Value = _windowOpacity * 100;
            BrowserOpacityLabel.Text = $"{_windowOpacity * 100:0}%";
            if (!_floatingApplied && settings.Bounds is { } bounds)
            {
                Width = Math.Clamp(bounds.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
                Height = Math.Clamp(bounds.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
                Left = Math.Clamp(bounds.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
                Top = Math.Clamp(bounds.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            _floatingApplied = true;
            UpdateInteractionHint();
        }
        finally { _applyingFloating = false; }
    }

    public void ToggleInteraction() => SetInteraction(IsClickThrough);

    public void SetInteraction(bool interactive)
    {
        if (interactive == !IsClickThrough) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (interactive)
        {
            var foreground = GetForegroundWindow();
            if (foreground != handle) _previousForeground = foreground;
        }
        IsClickThrough = !interactive;
        if (!interactive) { BrowserAppearancePopup.IsOpen = false; BrowserBookmarksPopup.IsOpen = false; }
        ShowActivated = interactive;
        UpdateInteractionStyles();
        UpdateInteractionHint();
        if (interactive && IsVisible) Activate();
        else if (handle != 0 && GetForegroundWindow() == handle && _previousForeground != 0 && IsWindow(_previousForeground))
            _ = SetForegroundWindow(_previousForeground);
    }

    private void UpdateInteractionHint()
    {
        PinButton.Content = IsClickThrough ? "\uE890" : "\uE7C9";
        PinButton.ToolTip = $"当前{(IsClickThrough ? "观看穿透" : "可交互")}；{_interactionHotkey} 切换模式";
    }

    internal static int InteractionStyle(int existing, bool passThrough) => passThrough
        ? existing | 0x20 | 0x08000000
        : existing & ~0x20 & ~0x08000000;

    private void UpdateInteractionStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) _ = SetWindowLong(handle, -20, InteractionStyle(GetWindowLong(handle, -20), IsClickThrough));
    }

    private nint FloatingHook(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (IsClickThrough && message == 0x84) { handled = true; return new nint(-1); }
        if (IsClickThrough && message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }

    private void ScheduleFloatingSave()
    {
        if (_applyingFloating || !_floatingApplied || !IsVisible) return;
        _floatingSave.Stop(); _floatingSave.Start();
    }

    private void OnBrowserOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrowserOpacityLabel is null || _applyingFloating) return;
        _windowOpacity = Math.Clamp(e.NewValue / 100, 0.2, 1);
        ApplyWindowOpacity();
        BrowserOpacityLabel.Text = $"{_windowOpacity * 100:0}%";
        ScheduleFloatingSave();
    }

    private void ApplyWindowOpacity()
    {
        // AllowsTransparency makes WPF own the layered HWND and its alpha.
        // Composition WebView2 participates in that surface (no child HWND).
        Opacity = _windowOpacity;
    }

    private async void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = BrowserAddressInput.Text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
        { FooterText.Text = "请输入有效的 HTTPS 网址。"; return; }
        await EnsureBrowserAsync(CancellationToken.None);
        _browser.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    private async void OnBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (_addBookmark is null || !Uri.TryCreate(CurrentAddress, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { await _addBookmark(_browser.CoreWebView2?.DocumentTitle ?? uri.Host, uri); FooterText.Text = "已保存收藏书签，可在站点列表或设置中管理。"; }
        catch (IOException) { FooterText.Text = "收藏未保存，请重试。"; }
    }

    private void OnBrowserAppearanceClick(object sender, RoutedEventArgs e)
    {
        BrowserBookmarksPopup.IsOpen = false;
        BrowserAppearancePopup.IsOpen = !BrowserAppearancePopup.IsOpen;
    }

    private void OnBrowserBookmarksClick(object sender, RoutedEventArgs e)
    {
        BrowserAppearancePopup.IsOpen = false;
        BrowserBookmarksPopup.IsOpen = !BrowserBookmarksPopup.IsOpen;
    }

    private async void OnBrowserBookmarkSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BrowserBookmarksList.SelectedItem is not BrowserSite site) return;
        BrowserBookmarksPopup.IsOpen = false;
        BrowserBookmarksList.SelectedItem = null;
        try
        {
            await ShowSiteAsync(site, CancellationToken.None);
            await _selectedSiteChanged(site);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or COMException)
        { FooterText.Text = "书签未能打开或保存，请重试。"; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
