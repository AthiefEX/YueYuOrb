using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TextBlock _userDataStatus = null!;
    private TextBlock _backgroundStatus = null!;
    private bool _userDataBusy;

    private void InitializeUserDataAndBackgroundSettings()
    {
        var general = SettingsTabControl.Items.OfType<TabItem>().First(tab => AutomationProperties.GetName(tab) == "常规" ||
            tab.Header is Panel header && header.Children.OfType<TextBlock>().Any(text => text.Text == "常规"));
        TabControl categories;
        if (general.Content is TabControl existing) categories = existing;
        else
        {
            var original = (UIElement)general.Content; general.Content = null;
            categories = CategoryTabs(); AddCategory(categories, "基本设置", original); general.Content = categories;
        }
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("自定义图片背景"));
        panel.Children.Add(Description("设置与工具面板使用裁切后的图片背景；图片透明度独立于文字和按钮。"));
        var enabled = new CheckBox { Content = "启用自定义背景", Margin = new Thickness(0, 12, 0, 8), Style = (Style)FindResource("ToggleSwitchStyle") };
        enabled.SetBinding(CheckBox.IsCheckedProperty, new Binding("CustomBackgroundEnabled") { Mode = BindingMode.TwoWay }); panel.Children.Add(enabled);
        var slider = new Slider { Minimum = 0, Maximum = 100, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        slider.SetResourceReference(StyleProperty, "SettingsSliderStyle");
        slider.SetBinding(Slider.ValueProperty, new Binding("CustomBackgroundOpacityPercent") { Mode = BindingMode.TwoWay });
        panel.Children.Add(Description("背景图片不透明度（0%–100%，应用后生效）")); panel.Children.Add(slider);
        panel.Children.Add(ButtonRow(Button("选择并裁切图片", OnChooseBackground), Button("恢复原主题背景", async (_, _) =>
        {
            _viewModel.CustomBackgroundEnabled = false;
            var result = await _settings.UpdateAsync(current => current with { CustomBackground = current.CustomBackground with { Enabled = false } }, CancellationToken.None);
            _backgroundStatus.Text = result.Success ? "已恢复原主题背景。" : "设置未保存，请重试。";
        })));
        _backgroundStatus = Description("选择图片后可以自由框选裁切；图片更换后立即生效。");
        panel.Children.Add(_backgroundStatus);
        var backgroundCard = Card(); backgroundCard.Child = panel;
        AppendToGeneralCategory(categories, "启动与外观", backgroundCard);
        panel = new StackPanel();
        panel.Children.Add(SectionTitle("普通用户数据"));
        panel.Children.Add(Description("备份普通配置、笔记、图片、收藏书签和助手聊天/素材/记忆。不包含密钥/密码库、录音、网页登录状态、机器证书、虚拟机磁盘与虚拟机配置。导入和删除在下次启动时执行，不自动重启，也不停止当前虚拟机。导入的助手和本地/全局任务默认关闭，助手操作权限清零。"));
        panel.Children.Add(ButtonRow(Button("导出用户数据", OnExportUserData), Button("导入用户数据", OnImportUserData), Button("删除用户数据", OnDeleteUserData)));
        _userDataStatus = Description("导入/删除前请先导出备份。密钥、网页登录状态和虚拟机相关数据保持不变。");
        if (File.Exists(Path.Combine(_paths.Root, "user-maintenance", "failed.json")))
            _userDataStatus.Text = "上次导入或删除未完成，已停止自动重试。请保留 user-maintenance 文件夹，检查备份后重新操作。";
        panel.Children.Add(_userDataStatus);
        var card = Card(); card.Child = panel;
        AppendToGeneralCategory(categories, "配置文件", card);
        var browser = LogicalDescendants(SettingsTabControl).OfType<TabItem>().FirstOrDefault(tab => AutomationProperties.GetName(tab) == "悬浮浏览器" || tab.Header?.ToString() == "悬浮浏览器");
        if (browser?.Content is FrameworkElement browserContent)
        {
            if (browserContent is ScrollViewer existingBrowserScroll && existingBrowserScroll.Content is FrameworkElement browserBody) { existingBrowserScroll.Content = null; browserContent = browserBody; }
            browser.Content = null; browserContent.MinHeight = 640;
            BrowserSitesGrid.MinHeight = 180; BrowserSitesGrid.MaxHeight = 400;
            browser.Content = new ScrollViewer { Content = browserContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
    }

    private void AppendToGeneralCategory(TabControl categories, string title, UIElement content)
    {
        var category = categories.Items.OfType<TabItem>().FirstOrDefault(item => item.Header?.ToString() == title);
        if (category?.Content is not ScrollViewer scroll || scroll.Content is not UIElement existing)
        { AddCategory(categories, title, content); return; }
        scroll.Content = null;
        var combined = new StackPanel(); combined.Children.Add(existing);
        if (content is FrameworkElement element) element.Margin = new Thickness(0, 16, 0, 0);
        combined.Children.Add(content); scroll.Content = combined;
    }

    private async void OnChooseBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var crop = new BackgroundCropWindow(dialog.FileName) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (crop.ShowDialog() != true) return;
            crop.Save(Path.Combine(_paths.Assets, "background.png"));
            _viewModel.CustomBackgroundEnabled = true;
            var result = await _settings.UpdateAsync(current => current with { CustomBackground = current.CustomBackground with { Enabled = true, Opacity = _viewModel.CustomBackgroundOpacityPercent / 100 } }, CancellationToken.None);
            _backgroundStatus.Text = result.Success ? "背景已保存并启用。" : "图片已保存，设置未保存，请点击应用重试。";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { _backgroundStatus.Text = "图片无法使用：" + error.Message; }
    }

    private async void OnExportUserData(object sender, RoutedEventArgs e)
    {
        if (_userDataBusy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "月语普通用户备份|*.yueyu.zip", FileName = "YueYu-user-data.yueyu.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await UserDataActionAsync(async () => { await UserDataArchive.ExportAsync(_paths.Root, _settings.Current, dialog.FileName, CancellationToken.None); _userDataStatus.Text = "普通用户数据备份已导出。"; });
    }

    private async void OnImportUserData(object sender, RoutedEventArgs e)
    {
        if (_userDataBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "月语普通用户备份|*.yueyu.zip;*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || !ConfirmDataChange("导入用户数据", "将在下次启动时替换普通配置、笔记、图片、收藏和助手聊天/素材/记忆。虚拟机、相关任务及敏感凭据保留；导入的助手和本地/全局任务默认停用，助手权限清零。继续吗？")) return;
        await UserDataActionAsync(async () => { await UserDataArchive.QueueImportAsync(_paths.Root, dialog.FileName, CancellationToken.None); _userDataStatus.Text = "导入已安排，下次启动时执行；当前功能继续运行。"; });
    }

    private async void OnDeleteUserData(object sender, RoutedEventArgs e)
    {
        if (_userDataBusy || !ConfirmDataChange("删除普通用户数据", "将在下次启动时删除笔记、悬浮球/背景图片、收藏、助手聊天/素材/记忆及普通设置，恢复默认值。不会删除虚拟机、虚拟机配置、密码库或网页登录状态。请先导出需要保留的数据。确认删除？")) return;
        await UserDataActionAsync(async () => { await UserDataArchive.QueueResetAsync(_paths.Root, CancellationToken.None); _userDataStatus.Text = "删除已安排，下次启动时执行。请在退出软件前确认已导出需要的数据。"; });
    }

    private async Task UserDataActionAsync(Func<Task> action)
    {
        _userDataBusy = true;
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        { _userDataStatus.Text = "数据操作未完成：" + error.Message; }
        finally { _userDataBusy = false; }
    }

    private bool ConfirmDataChange(string title, string text)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 490, Height = 255, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22) });
        var cancel = Button("取消", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true;
        var confirm = Button("确认", (_, _) => dialog.DialogResult = true);
        panel.Children.Add(ButtonRow(cancel, confirm)); dialog.Content = panel; GlassWindowFrame.WrapStandardWindow(dialog);
        return dialog.ShowDialog() == true;
    }
}
