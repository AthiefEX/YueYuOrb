using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Assistant;

namespace FloatDesk.App.Views;

internal static class AssistantAssetEditor
{
    public static AssistantAsset? Edit(Window owner, string kind, AssistantAsset? previous = null)
    {
        var window = new Window { Owner = owner, Title = previous is null ? "手动添加" + (kind == "role" ? "角色卡" : "Skill") : "编辑素材", Width = 700, Height = 620, MinWidth = 440, MinHeight = 360, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) }); window.SetResourceReference(Window.ForegroundProperty, "SettingsInkBrush");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var name = new TextBox { Text = previous?.Name ?? "", MaxLength = 100, Margin = new Thickness(0, 6, 0, 10), ToolTip = "名称" }; name.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle"); DockPanel.SetDock(name, Dock.Top); panel.Children.Add(name);
        var description = new TextBlock { Text = "名称在上方，正文在下方。只保存文本，不执行其中脚本；角色最多启用一个。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(description, Dock.Top); panel.Children.Add(description);
        var body = new TextBox { Text = previous?.Text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 64000 }; body.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle");
        var save = new Button { Content = "保存", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 8, 6, 0) }; save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryActionButtonStyle"); save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(body.Text)) { description.Text = "请填写名称和正文。"; return; } window.DialogResult = true; };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 8, 0, 0) }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButtonStyle");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(save); actions.Children.Add(cancel); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions); panel.Children.Add(body); window.Content = panel; GlassWindowFrame.WrapStandardWindow(window);
        return window.ShowDialog() == true ? new(previous?.Id ?? Guid.NewGuid(), name.Text.Trim(), kind, body.Text, previous?.SourcePath ?? "manual", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Text.Trim() + "\n" + kind + "\n" + body.Text)))) : null;
    }
}
