using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FloatDesk.Core.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private CheckBox _assistantUseChat = null!;
    private CheckBox _assistantUseSearch = null!;
    private bool _assistantRoleUpdating;
    private readonly Dictionary<(Guid Key, string Endpoint, string Model), string> _assistantModelHealth = [];
    private sealed record ConnectionModelRow(string Id, string State);

    private void BuildAssistantProfileRows(AssistantSettings settings)
    {
        _assistantRoleUpdating = true; _assistantProfileRows.Items.Clear();
        foreach (var profile in settings.Profiles)
        {
            var row = new Grid { Tag = profile, Margin = new Thickness(2, 3, 2, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
            for (var index = 0; index < 3; index++) row.ColumnDefinitions.Add(new() { Width = new GridLength(54) });
            var name = new TextBlock { Text = profile.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = profile.Name }; row.Children.Add(name);
            var model = new CheckBox { IsChecked = profile.Enabled && profile.ModelEnabled, ToolTip = profile.Name + " · 模型开关", Tag = profile.Id, HorizontalAlignment = HorizontalAlignment.Center };
            model.SetResourceReference(StyleProperty, "ToggleSwitchStyle"); Grid.SetColumn(model, 1); row.Children.Add(model); if (profile.Id == _editingAssistantProfile?.Id) _assistantModelEnabled = model;
            model.Click += (_, _) => _ = AssistantActionAsync(async () => { _assistantTest?.Cancel(); var enabled = model.IsChecked == true; await CommitAssistantProfilesAsync(current => current with { Profiles = current.Profiles.Select(item => item.Id == profile.Id ? item with { Enabled = enabled || item.Enabled, ModelEnabled = enabled } : item).ToArray() }); RefreshAssistantProfiles(profile.Id); });
            var search = new CheckBox { IsChecked = settings.SearchEnabled && settings.SearchProfileId == profile.Id, ToolTip = profile.Name + " · 搜索开关", HorizontalAlignment = HorizontalAlignment.Center }; search.SetResourceReference(StyleProperty, "ToggleSwitchStyle"); Grid.SetColumn(search, 2); row.Children.Add(search);
            search.Click += (_, _) => _ = ChangeSearchRoleAsync(profile, profile.Model, search.IsChecked == true);
            var refresh = ConnectionIcon("\uE72C", "读取/刷新模型", (_, _) => _ = AssistantActionAsync(async () => { ShowAssistantProfile(profile); await DiscoverAssistantModelsAsync(profile); })); Grid.SetColumn(refresh, 3); row.Children.Add(refresh);
            _assistantProfileRows.Items.Add(row);
        }
        _assistantRoleUpdating = false;
    }

    private static Button ConnectionIcon(string glyph, string hint, RoutedEventHandler handler)
    {
        var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14 }, ToolTip = hint, Width = 34, Height = 32, MinWidth = 0, Padding = new Thickness(4), Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center };
        button.SetResourceReference(StyleProperty, "ToolbarButtonStyle"); button.Click += handler; System.Windows.Automation.AutomationProperties.SetName(button, hint); return button;
    }

    private void RefreshConnectionModels()
    {
        if (_assistantAvailableModels is null || _editingAssistantProfile is not { } profile) return;
        var settings = AssistantConnections.WithLegacyProfile(_settings.Current.Assistant); var selected = _assistantManualModel.Text;
        _assistantModelEnabled = _assistantProfileRows.Items.OfType<Grid>().Where(row => row.Tag is AssistantApiProfile item && item.Id == profile.Id).SelectMany(row => row.Children.OfType<CheckBox>()).FirstOrDefault(toggle => toggle.Tag is Guid);
        var chat = settings.ChatModelId.Length > 0 ? settings.ChatModelId : settings.Profiles.FirstOrDefault(item => item.Id == settings.ActiveProfileId)?.Model;
        var search = settings.SearchModelId.Length > 0 ? settings.SearchModelId : settings.Profiles.FirstOrDefault(item => item.Id == settings.SearchProfileId)?.Model;
        var models = _assistantDiscoveredModels.GetValueOrDefault((profile.Id, profile.Endpoint)) ?? [];
        _assistantRoleUpdating = true;
        _assistantAvailableModels.ItemsSource = models.Select(model => new ConnectionModelRow(model,
            _assistantModelHealth.GetValueOrDefault((profile.Id, profile.Endpoint, model), "未检测") +
            (settings.ChatModelEnabled && settings.ActiveProfileId == profile.Id && chat == model ? " · 聊天" : "") +
            (settings.SearchEnabled && settings.SearchProfileId == profile.Id && search == model ? " · 搜索" : ""))).ToArray();
        _assistantAvailableModels.SelectedItem = _assistantAvailableModels.Items.OfType<ConnectionModelRow>().FirstOrDefault(item => item.Id == selected);
        _assistantProfileRows.SelectedItem = _assistantProfileRows.Items.OfType<Grid>().FirstOrDefault(row => row.Tag is AssistantApiProfile item && item.Id == profile.Id);
        _assistantRoleUpdating = false; UpdateConnectionRoleSwitches();
    }

    private void UpdateConnectionRoleSwitches()
    {
        if (_assistantUseChat is null || _assistantUseSearch is null) return;
        var settings = AssistantConnections.WithLegacyProfile(_settings.Current.Assistant); var model = _assistantManualModel.Text;
        _assistantRoleUpdating = true;
        _assistantUseChat.IsChecked = settings.ChatModelEnabled && settings.ActiveProfileId == _editingAssistantProfile?.Id && (settings.ChatModelId.Length > 0 ? settings.ChatModelId : _editingAssistantProfile?.Model) == model;
        _assistantUseSearch.IsChecked = settings.SearchEnabled && settings.SearchProfileId == _editingAssistantProfile?.Id && (settings.SearchModelId.Length > 0 ? settings.SearchModelId : _editingAssistantProfile?.Model) == model;
        _assistantProfileSummary.Text = $"聊天：{(settings.ChatModelId.Length > 0 ? settings.ChatModelId : settings.Model)}　搜索：{(settings.SearchEnabled ? settings.SearchModelId.Length > 0 ? settings.SearchModelId : settings.Profiles.FirstOrDefault(item => item.Id == settings.SearchProfileId)?.Model : "关闭")}";
        _assistantRoleUpdating = false;
    }

    private async Task ChangeSearchRoleAsync(AssistantApiProfile profile, string model, bool enabled)
    {
        try
        {
            if (enabled && (!profile.Enabled || !profile.ModelEnabled || string.IsNullOrWhiteSpace(model))) throw new InvalidOperationException("请先启用模型并选择有效模型ID。");
            await CommitAssistantProfilesAsync(current => AssistantWorkspacePolicy.SetSearch(current, profile.Id, model, enabled));
            _viewModel.AssistantSearchEnabled = _settings.Current.Assistant.SearchEnabled;
        }
        catch (InvalidOperationException error) { AssistantTextDialog.Show(this, "联网搜索模型", error.Message, editable: false); }
        RefreshAssistantProfiles(profile.Id); _assistantManualModel.Text = model; RefreshConnectionModels();
    }

    private static DataTemplate ConnectionModelTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel)); panel.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 5, 4, 5)); panel.SetValue(DockPanel.LastChildFillProperty, true);
        var state = new FrameworkElementFactory(typeof(TextBlock)); state.SetBinding(TextBlock.TextProperty, new Binding("State")); state.SetValue(DockPanel.DockProperty, Dock.Right); state.SetValue(UIElement.OpacityProperty, .7); state.SetValue(TextBlock.FontSizeProperty, 11d); state.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0)); panel.AppendChild(state);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding("Id")); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }
}
