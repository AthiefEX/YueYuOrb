using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FloatDesk.App.Danmaku;
using FloatDesk.App.ViewModels;
using FloatDesk.Core.Danmaku;

namespace FloatDesk.App.Views;

public sealed class DanmakuSettingsPanel : UserControl
{
    private readonly StackPanel _stack = new() { Margin = new Thickness(16) };
    private readonly TextBlock _status;
    private DanmakuOverlayWindow? _preview;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    public DanmakuSettingsPanel()
    {
        Content = new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label("消息弹幕 · 轻量通知版", true);
        Label("仅显示 Windows 实际提供的 QQ / TIM / 微信新通知。免打扰、隐藏内容或未产生系统通知时无法显示；标题筛选不代表精准群识别。私聊插队、图片及真正的 @ 标记暂不支持。");
        Toggle("启用消息弹幕（应用后生效）", "Enabled"); Toggle("在轮盘显示消息弹幕开关", "ShowInWheel");
        _status = Label(WindowsNotificationSource.HasPackageIdentity ? "点击授权后接收新通知" : "当前为便携模式：接收消息需安装通知增强包，预览不受影响。");
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => _status.Text = WindowsNotificationSource.HasPackageIdentity ? DanmakuLiveStatus.Text : "当前为便携版：只能预览；请退出并从开始菜单打开通知增强版。";
        Loaded += (_, _) => _statusTimer.Start();
        Unloaded += (_, _) => _statusTimer.Stop();
        var test = ActionButton("播放测试弹幕（不读取消息）");
        test.Click += (_, _) =>
        {
            if (DanmakuLiveStatus.PlayTest?.Invoke() != true)
                _status.Text = "测试暂未播放：请停止字幕翻译、解除暂停，或等待现有弹幕结束。";
        };
        var access = ActionButton("授权通知访问");
        access.Click += async (_, _) =>
        {
            access.IsEnabled = false;
            try { var source = new WindowsNotificationSource(); await source.RequestAccessAsync(); _status.Text = source.Status; }
            finally { access.IsEnabled = true; }
        };
        Label("消息来源", true); Toggle("QQ", "QQ"); Toggle("TIM", "TIM"); Toggle("微信", "WeChat");
        Field("通知标题包含（每行一项，留空显示所有已选来源）", "TitleFilter", true);
        Field("高亮关键词（每行一项，匹配标题或正文）", "HighlightKeywords", true);
        Label("显示方式与外观", true);
        Toggle("纵向滚动（关闭时横向飘过）", "Vertical"); Toggle("显示应用和通知标题", "ShowTitle"); Toggle("减少动画，使用静态显示", "ReduceMotion");
        var fonts = new ComboBox { IsEditable = true, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToArray(), Margin = new Thickness(0, 4, 0, 10) };
        Label("字体"); Bind(fonts, ComboBox.TextProperty, "FontFamily"); _stack.Children.Add(fonts);
        Field("字体大小（8–96）", "FontSize"); Field("文字颜色（#RRGGBB）", "Color"); Field("高亮颜色（高亮同时加粗）", "HighlightColor");
        Field("不透明度（0.1–1）", "Opacity"); Field("每条显示时长（3–30 秒）", "DurationSeconds");
        Label("显示区域", true);
        var monitor = new ComboBox { DisplayMemberPath = "", Margin = new Thickness(0, 4, 0, 10) };
        monitor.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("DanmakuMonitors")); Bind(monitor, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "MonitorId");
        Label("显示器（未选择时使用主屏幕）"); _stack.Children.Add(monitor);
        Field("区域宽度", "WidthDip"); Field("区域高度", "HeightDip");
        Field("水平位置（0=左侧，1=右侧）", "RelativeX"); Field("垂直位置（0=顶部，1=底部）", "RelativeY");
        var preview = ActionButton("预览并调整区域"); preview.Click += (_, _) => OpenPreview();
        Label("快捷键（留空不注册）", true);
        Field("开启 / 关闭弹幕", "ToggleHotkey"); Field("暂停 / 继续弹幕", "PauseHotkey"); Field("清空当前弹幕", "ClearHotkey");
        Label("预览框可移动和缩放，点击“完成”写回当前设置，最后点击“应用”保存。颜色、字号和区域预览使用本地模拟消息。");
        Unloaded += (_, _) => { _preview?.Close(); _preview = null; };
    }
    private void OpenPreview()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _preview?.Close();
        var window = new DanmakuOverlayWindow(); _preview = window;
        window.ApplySettings(vm.GetDanmakuPreview()); window.SetEditing(true);
        window.RegionEdited += area => { vm.DanmakuMonitorId = area.MonitorId; vm.DanmakuRelativeX = area.RelativeX; vm.DanmakuRelativeY = area.RelativeY; vm.DanmakuWidthDip = Math.Round(area.WidthDip); vm.DanmakuHeightDip = Math.Round(area.HeightDip); };
        window.Closed += (_, _) => { if (ReferenceEquals(_preview, window)) _preview = null; };
        window.Present(new("preview", "TIM", "通知预览", "这是一条模拟消息，不会读取聊天内容。"), false);
        window.Present(new("highlight", "微信", "提醒预览", "高亮消息使用加粗和独立颜色。"), true);
    }
    private TextBlock Label(string text, bool heading = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, heading ? 18 : 6, 0, 6), FontSize = heading ? 18 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        block.SetResourceReference(StyleProperty, heading ? "SettingsSectionTitleStyle" : "SettingsDescriptionStyle"); _stack.Children.Add(block); return block;
    }
    private void Toggle(string label, string property)
    {
        var item = new CheckBox { Content = label, Margin = new Thickness(0, 6, 0, 6) };
        item.SetResourceReference(StyleProperty, "ToggleSwitchStyle"); Bind(item, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, property); _stack.Children.Add(item);
    }
    private void Field(string label, string property, bool multiline = false)
    {
        Label(label); var input = new TextBox { Margin = new Thickness(0, 3, 0, 8), AcceptsReturn = multiline, MinHeight = multiline ? 64 : 30 };
        input.SetResourceReference(StyleProperty, "SettingsTextBoxStyle"); Bind(input, TextBox.TextProperty, property); _stack.Children.Add(input);
    }
    private Button ActionButton(string label)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        button.SetResourceReference(StyleProperty, "ActionButtonStyle"); _stack.Children.Add(button); return button;
    }
    private static void Bind(FrameworkElement target, DependencyProperty property, string name) =>
        target.SetBinding(property, new Binding("Danmaku" + name) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
}
