using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Assistant;

namespace FloatDesk.App.Views;

internal static class AssistantMemoryEditor
{
    private sealed record Choice(string Key, string Name);
    public static AssistantMemory? Edit(Window owner, AssistantMemory memory, IReadOnlyList<AssistantProject> projects)
    {
        var window = new Window { Owner = owner, Title = "记忆与来源", Width = 650, Height = 720, MinWidth = 460, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new() { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) }); window.SetResourceReference(Window.ForegroundProperty, "SettingsInkBrush");
        var root = new DockPanel { Margin = new Thickness(18) }; var body = new StackPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButtonStyle");
        var save = new Button { Content = "保存记忆" }; save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryActionButtonStyle"); buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        void Label(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 5) });
        ComboBox Select(string label, object source, string display, string value, object? selected)
        { Label(label); var combo = new ComboBox { ItemsSource = source as System.Collections.IEnumerable, DisplayMemberPath = display, SelectedValuePath = value, SelectedValue = selected }; combo.SetResourceReference(FrameworkElement.StyleProperty, "SettingsComboBoxStyle"); body.Children.Add(combo); return combo; }
        var scope = Select("所属项目（全局记忆会用于所有项目）", projects.Prepend(new(Guid.Empty, "全局", DateTimeOffset.MinValue)).ToArray(), "Name", "Id", memory.ProjectId ?? Guid.Empty); scope.IsEnabled = memory.SourceMessageId is null;
        var kind = Select("类型", new[] { new Choice("profile", "个人事实"), new Choice("preference", "长期偏好"), new Choice("habit", "行为习惯"), new Choice("episode", "事件"), new Choice("goal", "目标"), new Choice("project_fact", "项目事实") }, "Name", "Key", memory.Kind);
        Label("记忆分类标识（如theme，不是API密钥；同项目冲突需审核）"); var key = new TextBox { Text = memory.Key, MaxLength = 200 }; key.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle"); body.Children.Add(key);
        Label("内容（最多4000字符）"); var content = new TextBox { Text = memory.Content, MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; content.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle"); body.Children.Add(content);
        var policy = Select("云端使用策略", new[] { new Choice("deny", "仅本地，禁止云端"), new Choice("ask", "云端发送前确认"), new Choice("allow", "允许发送相关片段") }, "Name", "Key", memory.CloudPolicy);
        Label("重要性（0–1）"); var importance = new Slider { Minimum = 0, Maximum = 1, Value = memory.Importance, TickFrequency = .1, IsSnapToTickEnabled = true }; importance.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSliderStyle"); body.Children.Add(importance);
        Label("过期日期（可选，yyyy-MM-dd；过期后不参与召回）"); var expires = new TextBox { Text = memory.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", MaxLength = 10 }; expires.SetResourceReference(FrameworkElement.StyleProperty, "SettingsTextBoxStyle"); body.Children.Add(expires);
        Label($"修订{memory.Revision} · 置信度{memory.Confidence:P0} · {(memory.SourceMessageId is null ? "手动记忆" : "有消息来源，项目不可脱离来源修改")}"); var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; body.Children.Add(status);
        AssistantMemory? result = null;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(content.Text)) { status.Text = "记忆内容不能为空。"; return; }
            DateTimeOffset? expiry = null;
            if (expires.Text.Length > 0)
            { if (!DateOnly.TryParseExact(expires.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { status.Text = "过期日期格式无效。"; return; } expiry = new DateTimeOffset(date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Local)); }
            var project = scope.SelectedValue is Guid id && id != Guid.Empty ? id : (Guid?)null;
            result = memory with { Content = content.Text.Trim(), Key = key.Text.Trim(), ProjectId = project, Kind = kind.SelectedValue as string ?? "preference", CloudPolicy = policy.SelectedValue as string ?? "ask", Importance = importance.Value, ExpiresAt = expiry }; window.DialogResult = true;
        };
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); window.Content = root; GlassWindowFrame.WrapStandardWindow(window);
        return window.ShowDialog() == true ? result : null;
    }
}
