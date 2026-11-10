using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using FloatDesk.App.ViewModels;
using FloatDesk.Windows.Network;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private SteamNetworkController? _networkController;
    private TextBlock _networkStatus = null!;
    private TextBox _networkPacAddress = null!;
    private TextBlock _networkDiagnosis = null!;
    private TextBlock _networkCertificateStatus = null!;
    private bool _certificateStatusRefreshing;
    private DateTime _certificateStatusUpdated;
    private string? _networkActionError;
    private bool _networkBusy;
    private TabItem _networkTab = null!;
    private readonly DispatcherTimer _networkStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public void SelectNetworkPage() => SettingsTabControl.SelectedItem = _networkTab;

    private void InitializeNetworkSettings(SteamNetworkController? controller)
    {
        _networkController = controller;
        _networkTab = new TabItem { Header = "网络加速", Style = (Style)FindResource("NavigationTabItemStyle") };
        AutomationProperties.SetName(_networkTab, "网络加速");
        var tabs = CategoryTabs();
        tabs.Margin = new Thickness(28, 16, 28, 24);
        var settings = new StackPanel();
        settings.Children.Add(SectionTitle("Steam 定向加速"));
        settings.Children.Add(Description("无需额外软件。兼容模式直接接入 Steam 的网页连接，不依赖客户端采用系统代理。握手被重置时会对同一节点快速重试，减少首屏等待。"));
        settings.Children.Add(ButtonRow(
            NetworkAction("开始加速", StartNetworkAsync),
            NetworkAction("停止加速", () => controller?.StopAsync() ?? Task.CompletedTask)));
        settings.Children.Add(ButtonRow(
            NetworkAction("清除最近失败", () =>
            {
                controller?.ClearLastFailure();
                return Task.CompletedTask;
            })));
        _networkStatus = Description("");
        _networkStatus.Margin = new Thickness(0, 10, 0, 8);
        settings.Children.Add(_networkStatus);
        _networkCertificateStatus = Description("证书状态：正在检查…");
        AutomationProperties.SetName(_networkCertificateStatus, "网络加速证书状态");
        settings.Children.Add(_networkCertificateStatus);
        settings.Children.Add(ButtonRow(
            NetworkAction("安装证书", () => controller?.InstallCertificateAsync(CancellationToken.None) ?? Task.CompletedTask),
            NetworkAction("卸载证书", () => controller?.UninstallCertificateAsync(CancellationToken.None) ?? Task.CompletedTask)));
        settings.Children.Add(Description("首次安装需要管理员授权，将安装月语网络后台与计算机级信任证书。安装后日常开始、停止和重开软件均在后台完成；卸载证书会先停止加速并卸载专用网络后台。"));
        settings.Children.Add(NetworkToggle("Steam 兼容模式（证书 + hosts，推荐）", nameof(SettingsViewModel.NetworkCompatibility)));
        settings.Children.Add(Description("仅接入选定 Steam 域名。停止后还原 hosts 并删除叶密钥，计算机根证书保留至明确卸载。根私钥由后台服务保护且不可导出，不交给界面进程；解密内容不记录。"));
        settings.Children.Add(NetworkToggle("PAC 模式应用到系统（关闭兼容模式时生效）", nameof(SettingsViewModel.NetworkSystemPac)));
        settings.Children.Add(NetworkToggle("在轮盘显示一键加速", nameof(SettingsViewModel.NetworkInWheel)));
        settings.Children.Add(Description("点击开始会保存网络设置；其他改动请点击窗口下方“应用”。修改运行中的加速配置后需重新开始。"));
        settings.Children.Add(Description("手动 PAC 地址（仅 PAC 模式运行时有效）："));
        _networkPacAddress = new TextBox { IsReadOnly = true, Style = (Style)FindResource("SettingsTextBoxStyle"), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(_networkPacAddress, "本地 PAC 地址");
        settings.Children.Add(_networkPacAddress);
        settings.Children.Add(Description("上游仍验证可信证书链与原始域名。其他代理可能抢先接管连接；Steam DNS 缓存未更新时请重启 Steam。此功能不替代游戏下载加速，也无法保证完全中断的线路可用。"));
        var card = Card(); card.Child = settings;
        AddCategory(tabs, "一键加速", card);
        var sites = new StackPanel();
        sites.Children.Add(SectionTitle("站点与解析"));
        sites.Children.Add(NetworkToggle("Steam 商店与登录", nameof(SettingsViewModel.NetworkStore)));
        sites.Children.Add(NetworkToggle("Steam 社区与创意工坊页面", nameof(SettingsViewModel.NetworkCommunity)));
        sites.Children.Add(NetworkToggle("商店与社区图片资源", nameof(SettingsViewModel.NetworkContent)));
        sites.Children.Add(NetworkToggle("使用 HTTPS DNS 辅助解析", nameof(SettingsViewModel.NetworkHttpsDns)));
        sites.Children.Add(Description("独立解析并行查询 DNSPod / 360 / Cloudflare / Google 的 HTTPS DNS，不修改系统 DNS。兼容模式始终使用独立解析，防止 hosts 回环。此功能不改变网络出口。"));
        sites.Children.Add(NetworkToggle("网络阻断 SNI 时启用无 SNI 回退", nameof(SettingsViewModel.NetworkNoSniCompatibility)));
        sites.Children.Add(Description("仅对 Steam 兼容模式生效。先使用原始域名 SNI；握手在证书到达前被重置时，会立即在同一节点并试无 SNI 握手（此项关闭也会如此，否则该站点无法访问），开启后其它握手失败也会补一次无 SNI 尝试。无论模式如何，原始域名、可信证书链和有效期仍必须通过验证，证书被拒不会触发回退。"));
        var sitesCard = Card(); sitesCard.Child = sites;
        AddCategory(tabs, "站点与解析", sitesCard);
        var diagnosis = new StackPanel();
        diagnosis.Children.Add(SectionTitle("连通检测与恢复"));
        diagnosis.Children.Add(Description("入口响应不代表登录、订阅和下载全部正常。检测保留原站点证书验证，结果不包含账号或网页内容。“恢复网络配置”只恢复运行期修改；彻底移除后台与证书请点击“卸载证书”。"));
        diagnosis.Children.Add(ButtonRow(
            NetworkAction("检测直连", () => DiagnoseNetworkAsync(false)),
            NetworkAction("检测加速", () => DiagnoseNetworkAsync(true)),
            NetworkAction("恢复网络配置", () => controller?.RecoverRuntimeAsync(CancellationToken.None) ?? Task.CompletedTask)));
        _networkDiagnosis = Description("尚未检测。");
        _networkDiagnosis.Margin = new Thickness(0, 14, 0, 0);
        diagnosis.Children.Add(_networkDiagnosis);
        var diagnosticCard = Card(); diagnosticCard.Child = diagnosis;
        AddCategory(tabs, "检测与恢复", diagnosticCard);
        _networkTab.Content = tabs;
        SettingsTabControl.Items.Insert(Math.Max(0, SettingsTabControl.Items.Count - 1), _networkTab);
        if (controller is not null) controller.Changed += RefreshNetworkStatus;
        _networkStatusTimer.Tick += (_, _) => RefreshNetworkStatus();
        IsVisibleChanged += (_, _) => { if (IsVisible) _networkStatusTimer.Start(); else _networkStatusTimer.Stop(); };
        Closed += (_, _) => { _networkStatusTimer.Stop(); if (controller is not null) controller.Changed -= RefreshNetworkStatus; };
        RefreshNetworkStatus();
    }

    private CheckBox NetworkToggle(string text, string property)
    {
        var toggle = new CheckBox { Content = text, Margin = new Thickness(0, 14, 0, 0), Style = (Style)FindResource("ToggleSwitchStyle") };
        toggle.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return toggle;
    }

    private Button NetworkAction(string text, Func<Task> action)
    {
        var button = Button(text, async (_, _) =>
        {
            if (_networkBusy || _networkController is null || _networkController.IsBusy) return;
            _networkBusy = true;
            _networkActionError = null;
            _networkTab.IsEnabled = false;
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or SocketException or JsonException or OperationCanceledException or TimeoutException or System.Security.Cryptography.CryptographicException)
            { _networkActionError = error.Message; }
            finally { _networkBusy = false; _networkTab.IsEnabled = true; _certificateStatusUpdated = DateTime.MinValue; RefreshNetworkStatus(); }
        });
        return button;
    }

    private async Task StartNetworkAsync()
    {
        var result = await _settings.UpdateAsync(current => current with { SteamNetwork = _viewModel.BuildNetwork(current.SteamNetwork) }, CancellationToken.None);
        if (!result.Success) throw new IOException("网络设置未保存，请重试。");
        _networkStatus.Text = "正在后台启动…";
        await Task.Run(() => _networkController!.StartAsync(_settings.Current.SteamNetwork, CancellationToken.None));
    }

    private async Task DiagnoseNetworkAsync(bool accelerated)
    {
        if (accelerated && _networkController?.IsRunning != true) throw new InvalidOperationException("请先开始加速。");
        _networkDiagnosis.Text = "正在检测，请稍候…";
        var settings = accelerated ? _networkController!.ActiveSettings! : _viewModel.BuildNetwork(_settings.Current.SteamNetwork);
        _networkDiagnosis.Text = await _networkController!.DiagnoseAsync(settings, accelerated, CancellationToken.None);
    }

    private void RefreshNetworkStatus()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshNetworkStatus); return; }
        _networkStatus.Text = _networkController?.Status ?? "当前为界面预览，未连接网络运行时。";
        if (_networkActionError is not null) _networkStatus.Text += Environment.NewLine + _networkActionError;
        _networkPacAddress.Text = _networkController?.PacUri?.AbsoluteUri ?? "";
        _ = RefreshCertificateStatusAsync();
    }

    private async Task RefreshCertificateStatusAsync()
    {
        if (_certificateStatusRefreshing || DateTime.UtcNow - _certificateStatusUpdated < TimeSpan.FromSeconds(5)) return;
        _certificateStatusRefreshing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var status = await SteamBackgroundClient.StatusAsync(timeout.Token);
            _networkCertificateStatus.Text = "证书状态：" + status;
            _certificateStatusUpdated = DateTime.UtcNow;
        }
        finally { _certificateStatusRefreshing = false; }
    }
}
