using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

internal static class AssistantTextDialog
{
    public static string? Show(Window? owner, string title, string text, bool editable = true, bool confirmation = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var window = new Window { Owner = owner, Title = title, Width = 660, Height = 460, MinWidth = 440, MinHeight = 300, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        window.SetResourceReference(Window.ForegroundProperty, "SettingsInkBrush");
        var root = new DockPanel { Margin = new Thickness(18) }; var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = editable || confirmation ? "取消" : "关闭", IsCancel = true, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButtonStyle"); actions.Children.Add(cancel);
        var accept = new Button { Content = editable ? "确认" : "同意本次操作", Padding = new Thickness(16, 7, 16, 7) }; accept.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryActionButtonStyle"); accept.Click += (_, _) => window.DialogResult = true; if (editable || confirmation) actions.Children.Add(accept);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var input = new TextBox { Text = text, IsReadOnly = !editable, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), MaxLength = editable ? 4000 : int.MaxValue };
        input.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle"); root.Children.Add(input); window.Content = root; GlassWindowFrame.WrapStandardWindow(window);
        using var registration = token.Register(() => window.Dispatcher.BeginInvoke(() => { if (window.IsVisible) window.DialogResult = false; }));
        return window.ShowDialog() == true ? input.Text : null;
    }

    public static bool Confirm(Window? owner, string title, string text, CancellationToken token = default) => Show(owner, title, text, editable: false, confirmation: true, token: token) is not null;
}
