using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

internal static class SessionCloseConfirmation
{
    internal static bool Show(Window owner)
    {
        var dialog = new Window
        {
            Owner = owner, Title = "关闭桌面分身", Width = 440, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative)
        });
        dialog.SetResourceReference(Window.BackgroundProperty, "SettingsWindowBackgroundBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "SettingsInkBrush");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "关闭并清理分身？", FontSize = 19, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "将关闭本机分身服务、停止任务、释放键鼠并注销此分身，关闭其中的应用。请先保存工作；主桌面不受影响。系统服务操作可能请求管理员授权，取消授权会保留分身。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 20)
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "继续使用", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        var close = new Button { Content = "关闭并清理", Padding = new Thickness(14, 7, 14, 7) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "ActionButtonStyle");
        close.SetResourceReference(FrameworkElement.StyleProperty, "ActionButtonStyle");
        close.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(close);
        content.Children.Add(actions);
        var shell = new Border { Child = content };
        shell.SetResourceReference(FrameworkElement.StyleProperty, "ToolWindowShellStyle");
        dialog.Content = shell;
        GlassWindowFrame.Prepare(dialog);
        return dialog.ShowDialog() == true;
    }
}
