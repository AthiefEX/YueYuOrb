using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using FloatDesk.Core.Browser;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class AiBrowserWindow : Window, IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly Func<BrowserSite, Task> _selectedSiteChanged;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private WebView2 _browser;
    private CoreWebView2Environment? _environment;
    private BrowserSite[] _sites = [];
    private BrowserSite? _selectedSite;
    private bool _initialized;
    private bool _releaseOnClose;
    private bool _allowClose;
    private int _zoomPercent = 100;

    public AiBrowserWindow(
        AppDataPaths paths,
        Func<BrowserSite, Task> selectedSiteChanged)
    {
        InitializeComponent();
        _paths = paths;
        _selectedSiteChanged = selectedSiteChanged;
        _browser = CreateBrowser();
        BrowserHost.Children.Insert(0, _browser);
        Closing += OnClosing;
    }

    public string CurrentAddress => _browser.Source?.AbsoluteUri ?? "";

    public void ApplySettings(AppSettings settings)
    {
        _releaseOnClose = settings.BrowserReleaseOnClose;
        _zoomPercent = settings.BrowserZoomPercent;
        Topmost = settings.BrowserTopmost;
        PinButton.Content = Topmost ? "取消固定" : "固定";
        _sites = settings.BrowserSites.OrderBy(site => site.Order).ToArray();
        SiteComboBox.ItemsSource = _sites;
        var selected = _sites.FirstOrDefault(site => site.Id == settings.SelectedBrowserSiteId)
            ?? _sites.FirstOrDefault();
        if (selected is not null)
        {
            SiteComboBox.SelectedItem = selected;
            _selectedSite = selected;
        }
        if (_initialized)
            _browser.ZoomFactor = _zoomPercent / 100.0;
    }

    public async Task ShowAndEnsureAsync(CancellationToken cancellationToken)
    {
        if (!IsVisible)
            Show();
        Activate();
        await EnsureBrowserAsync(cancellationToken);
        if (_browser.CoreWebView2?.IsSuspended == true)
            _browser.CoreWebView2.Resume();
    }

    public async Task ShowSiteAsync(
        BrowserSite site,
        CancellationToken cancellationToken)
    {
        await ShowAndEnsureAsync(cancellationToken);
        _browser.CoreWebView2!.Navigate(site.Url.AbsoluteUri);
        _selectedSite = site;
        SiteComboBox.SelectedItem = site;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            return;
        try
        {
            await _browser.CoreWebView2!.ExecuteScriptAsync(
                "window.dispatchEvent(new Event('beforeunload'))");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
            // Navigation teardown can race with application shutdown.
        }
    }

    public async ValueTask DisposeBrowserAsync()
    {
        _allowClose = true;
        RebuildBrowser();
        if (IsVisible)
            Hide();
        _environment = null;
        await Task.CompletedTask;
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            StatusPanel.Visibility = Visibility.Visible;
            StatusTitle.Text = "正在启动网页";
            StatusDetail.Text = "首次打开需要初始化 WebView2。";
            Directory.CreateDirectory(_paths.WebViewData);
            var options = new CoreWebView2EnvironmentOptions();
            _environment ??= await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _paths.WebViewData,
                options);
            cancellationToken.ThrowIfCancellationRequested();
            await _browser.EnsureCoreWebView2Async(_environment);
            _browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            _browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
            _browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _browser.CoreWebView2.PermissionRequested += OnPermissionRequested;
            _browser.CoreWebView2.DownloadStarting += OnDownloadStarting;
            _browser.CoreWebView2.ProcessFailed += OnProcessFailed;
            _browser.ZoomFactor = _zoomPercent / 100.0;
            _initialized = true;
            StatusPanel.Visibility = Visibility.Collapsed;
            if (_selectedSite is not null &&
                _browser.Source is null)
            {
                _browser.CoreWebView2.Navigate(_selectedSite.Url.AbsoluteUri);
            }
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException or
            InvalidOperationException or
            COMException or
            UnauthorizedAccessException)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusTitle.Text = "WebView2 尚不可用";
            StatusDetail.Text =
                $"AI 网页无法启动，但悬浮球、笔记和截图仍可使用。\n{exception.Message}";
            FooterText.Text = "可安装或修复 Microsoft Edge WebView2 Runtime 后重试。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusTitle.Text = "网页初始化失败";
            StatusDetail.Text = $"AI 网页无法启动，但悬浮球、笔记和截图仍可使用。\n{exception.Message}";
            FooterText.Text = "可点击重试，或改用默认浏览器。";
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async void OnSiteSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SiteComboBox.SelectedItem is not BrowserSite site ||
            site == _selectedSite)
        {
            return;
        }
        if (_initialized &&
            _browser.Source is not null &&
            !string.Equals(
                _browser.Source.AbsoluteUri,
                site.Url.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                this,
                "切换站点会离开当前页面；尚未发送的输入可能丢失。继续吗？",
                "切换 AI 网页",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SiteComboBox.SelectedItem = _selectedSite;
                return;
            }
        }

        _selectedSite = site;
        await _selectedSiteChanged(site);
        await EnsureBrowserAsync(CancellationToken.None);
        if (_initialized)
            _browser.CoreWebView2.Navigate(site.Url.AbsoluteUri);
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (BrowserNavigationPolicy.Classify(e.Uri, userInitiated: true) ==
            NavigationDisposition.AllowHttps)
        {
            AddressText.Text = e.Uri;
            return;
        }

        e.Cancel = true;
        FooterText.Text = "已阻止非 HTTPS 顶层导航。";
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            AddressText.Text = _browser.Source?.AbsoluteUri ?? "";
            FooterText.Text = _browser.Source?.Host is { Length: > 0 } host
                ? $"当前站点：{host} · WebView2 独立进程"
                : "WebView2 独立进程";
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;
        StatusTitle.Text = "网页加载失败";
        StatusDetail.Text =
            $"错误：{e.WebErrorStatus}。可重试或用默认浏览器打开；外部浏览器不共享此处登录状态。";
    }

    private void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            e.Handled = true;
            FooterText.Text = "已阻止不安全的新窗口地址。";
            return;
        }

        e.Handled = true;
        var popup = new AiPopupWindow(uri, _environment!)
        {
            Owner = this
        };
        popup.Show();
    }

    private void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
        FooterText.Text = "网页权限请求默认已拒绝。";
    }

    private void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(e.ResultFilePath),
            Filter = "所有文件|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            e.ResultFilePath = dialog.FileName;
            return;
        }
        e.Cancel = true;
        FooterText.Text = "下载已取消。";
    }

    private void OnProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusTitle.Text = "网页进程已中断";
        StatusDetail.Text =
            $"{e.ProcessFailedKind}。点击重试可重建浏览器进程，登录 profile 保留。";
        FooterText.Text = "WebView2 进程崩溃；本地功能继续运行。";
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        RebuildBrowser();
        _environment = null;
        await EnsureBrowserAsync(CancellationToken.None);
    }

    private void OnInstallRuntimeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "无法打开 WebView2 下载页",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_initialized && _browser.CoreWebView2.CanGoBack)
            _browser.CoreWebView2.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_initialized && _browser.CoreWebView2.CanGoForward)
            _browser.CoreWebView2.GoForward();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            _browser.CoreWebView2.Reload();
    }

    private void OnExternalClick(object sender, RoutedEventArgs e)
    {
        var value = _browser.Source?.AbsoluteUri ?? _selectedSite?.Url.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(value))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = value,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开浏览器");
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Content = Topmost ? "取消固定" : "固定";
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e) =>
        SetZoom(_zoomPercent - 10);

    private void OnZoomInClick(object sender, RoutedEventArgs e) =>
        SetZoom(_zoomPercent + 10);

    private void SetZoom(int value)
    {
        _zoomPercent = Math.Clamp(value, 75, 150);
        if (_initialized)
            _browser.ZoomFactor = _zoomPercent / 100.0;
        FooterText.Text = $"网页缩放：{_zoomPercent}%";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        Hide();
        try
        {
            if (_initialized && _releaseOnClose)
            {
                await DisposeBrowserAsync();
                return;
            }
            if (!_initialized)
                return;
            var outcome = await BrowserSuspension.TrySuspendAsync(
                () => _browser.CoreWebView2?.IsSuspended ?? true,
                async () =>
                {
                    var browser = _browser.CoreWebView2;
                    return browser is not null && await browser.TrySuspendAsync();
                });
            FooterText.Text = outcome switch
            {
                BrowserSuspendOutcome.Suspended => "窗口已隐藏，网页已挂起。",
                BrowserSuspendOutcome.AlreadySuspended => "窗口已隐藏，网页保持挂起。",
                BrowserSuspendOutcome.CannotSuspend => "窗口已隐藏；网页当前不能被系统挂起。",
                _ => "窗口已隐藏；网页已结束或正在关闭。"
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException or ObjectDisposedException)
        {
            FooterText.Text = "窗口已隐藏；网页已结束或正在关闭。";
        }
    }

    private WebView2 CreateBrowser()
    {
        var browser = new WebView2();
        browser.NavigationStarting += OnNavigationStarting;
        browser.NavigationCompleted += OnNavigationCompleted;
        return browser;
    }

    private void RebuildBrowser()
    {
        BrowserHost.Children.Remove(_browser);
        _browser.Dispose();
        _browser = CreateBrowser();
        BrowserHost.Children.Insert(0, _browser);
        _initialized = false;
    }

    public void Dispose()
    {
        _initializationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class AiPopupWindow : Window, IDisposable
{
    private readonly WebView2 _browser = new();
    private bool _disposed;

    public AiPopupWindow(Uri uri, CoreWebView2Environment environment)
    {
        Width = 640;
        Height = 720;
        MinWidth = 380;
        MinHeight = 420;
        Title = $"登录 - {uri.Host}";
        var errorText = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var host = new System.Windows.Controls.Grid();
        host.Children.Add(_browser);
        host.Children.Add(errorText);
        Content = host;
        Loaded += async (_, _) =>
        {
            try
            {
                await _browser.EnsureCoreWebView2Async(environment);
                _browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                _browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
                _browser.CoreWebView2.PermissionRequested += (_, args) =>
                {
                    args.State = CoreWebView2PermissionState.Deny;
                    args.Handled = true;
                };
                _browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            }
            catch (Exception exception)
            {
                _browser.Visibility = Visibility.Collapsed;
                errorText.Text =
                    $"登录窗口无法启动。\n\n{exception.Message}\n\n" +
                    "可在关闭本窗口后，使用 AI 窗口中的“浏览器打开”。";
                errorText.Visibility = Visibility.Visible;
            }
        };
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }
}
