using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatDesk.App.ViewModels;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public sealed partial class AssistantWindow
{
    private readonly ListBox _sessionSidebar = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly HashSet<Guid> _mentionedSkills = [];
    private readonly TextBlock _mentionHint = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _targetCaption = new() { Text = "自动选择对象", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
    private ColumnDefinition _sidebarWidth = null!;
    private bool _updatingSidebar;
    private bool _composing;

    private static TextBlock Glyph(string glyph) => new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
    private static Button ChatIcon(string glyph, string hint, RoutedEventHandler action)
    {
        var button = new Button { Content = Glyph(glyph), Width = 34, Height = 34, MinWidth = 0, Padding = new Thickness(4), Margin = new Thickness(2), ToolTip = hint };
        ApplyStyle(button, "ToolbarIconButtonStyle"); AutomationProperties.SetName(button, hint); ToolTipService.SetInitialShowDelay(button, 500); button.Click += action; return button;
    }

    private void InitializeChatWorkspace(Action openSettings)
    {
        _surface = new Grid(); _sidebarWidth = new ColumnDefinition { Width = new GridLength(166) }; _surface.ColumnDefinitions.Add(_sidebarWidth); _surface.ColumnDefinitions.Add(new());
        var sidebar = new Grid { Margin = new Thickness(8, 8, 0, 8) }; sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto }); sidebar.RowDefinitions.Add(new()); sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.SetResourceReference(BackgroundProperty, "SettingsCardSubtleBrush");
        var create = ActionButton("＋ 新对话", async (_, _) => await NewSessionAsync()); create.Margin = new Thickness(5, 4, 5, 8); sidebar.Children.Add(create);
        _sessionSidebar.ItemContainerStyle = AssistantListStyle.Create(); _sessionSidebar.SetResourceReference(ForegroundProperty, "SettingsInkBrush"); Grid.SetRow(_sessionSidebar, 1); sidebar.Children.Add(_sessionSidebar);
        _sessionSidebar.SelectionChanged += (_, _) => { if (!_updatingSidebar && _sessionSidebar.SelectedItem is Grid { Tag: AssistantSession selected }) _sessions.SelectedItem = selected; };
        var preferences = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        preferences.Children.Add(ChatIcon("\uE713", "AI助手设置", (_, _) => openSettings()));
        var opacity = ChatIcon("\uE790", "窗口透明度", (_, _) => { }); opacity.Click += (_, _) => ShowOpacityPopup(opacity); preferences.Children.Add(opacity); Grid.SetRow(preferences, 2); sidebar.Children.Add(preferences); _surface.Children.Add(sidebar);
        var main = new Grid(); main.RowDefinitions.Add(new() { Height = GridLength.Auto }); main.RowDefinitions.Add(new()); main.RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetColumn(main, 1); _surface.Children.Add(main);
        var top = new StackPanel { Margin = new Thickness(8, 4, 10, 2) }; var topLine = new DockPanel();
        var fold = ChatIcon("\uE700", "展开/收起会话列表", (_, _) => { var visible = _sidebarWidth.Width.Value > 0; _sidebarWidth.Width = new GridLength(visible ? 0 : 166); sidebar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible; }); DockPanel.SetDock(fold, Dock.Left); topLine.Children.Add(fold);
        var settings = ChatIcon("\uE713", "设置", (_, _) => openSettings()); DockPanel.SetDock(settings, Dock.Right); topLine.Children.Add(settings);
        _connection.VerticalAlignment = VerticalAlignment.Center; _connection.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush"); topLine.Children.Add(_connection); top.Children.Add(topLine); main.Children.Add(top);
        _scroll.Content = _messages; _scroll.Margin = new Thickness(10, 0, 12, 0); Grid.SetRow(_scroll, 1); main.Children.Add(_scroll);
        var bottom = new StackPanel { Margin = new Thickness(10, 0, 12, 8) }; var context = new Grid(); context.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); context.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); context.ColumnDefinitions.Add(new()); context.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var target = ChatIcon("\uE710", "任务对象：自动 / 主桌面 / 分身", (_, _) => { }); target.Click += (_, _) => ShowTargetMenu(target); context.Children.Add(target);
        var skill = ChatIcon("\uE168", "@ 调用Skill（仅本次消息）", (_, _) => { }); skill.Content = new TextBlock { Text = "@", FontFamily = new FontFamily("Segoe UI"), FontSize = 18, FontWeight = FontWeights.SemiBold }; skill.Click += (_, _) => _ = ShowSkillMenuAsync(skill); Grid.SetColumn(skill, 1); context.Children.Add(skill);
        var summary = new Grid { ClipToBounds = true }; summary.ColumnDefinitions.Add(new()); summary.ColumnDefinitions.Add(new()); summary.Children.Add(_targetCaption); Grid.SetColumn(_mentionHint,1); summary.Children.Add(_mentionHint); Grid.SetColumn(summary,2); context.Children.Add(summary);
        var project = ChatIcon("\uE8B7", "资料项目", (_, _) => { }); Grid.SetColumn(project,3); context.Children.Add(project);
        var projectHost = new StackPanel(); InitializeAssistantKnowledgeToolbar(projectHost, top); var projectMenu = new ContextMenu(); projectMenu.Items.Add(new MenuItem { Header = projectHost, StaysOpenOnClick = true }); projectMenu.PlacementTarget = project; project.Click += (_, _) => projectMenu.IsOpen = true;
        bottom.Children.Add(context); ApplyStyle(_input, "SettingsTextBoxStyle"); _input.MinHeight = 60; bottom.Children.Add(_input);
        var commands = new DockPanel(); var voice = new StackPanel { Orientation = Orientation.Horizontal }; _record = ChatIcon("\uE720", "语音", (_, _) => VoiceRequested?.Invoke()); voice.Children.Add(_record); voice.Children.Add(ChatIcon("\uE74F", "停止朗读", (_, _) => StopSpeechRequested?.Invoke())); commands.Children.Add(voice);
        var send = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; send.Children.Add(ChatIcon("\uE71A", "停止", (_, _) => { _runtime.Cancel(); StopSpeechRequested?.Invoke(); }));
        _send = ChatIcon("\uE724", "发送", async (_, _) => await SendMessageAsync()); ApplyStyle(_send, "PrimaryActionButtonStyle"); _send.MinWidth = 0; send.Children.Add(_send); commands.Children.Add(send); bottom.Children.Add(commands);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush"); bottom.Children.Add(_status); Grid.SetRow(bottom, 2); main.Children.Add(bottom);
        _runtime.SetTarget(Guid.Empty);
    }

    private void ShowOpacityPopup(Button anchor)
    {
        if (_opacity.Parent is Panel old) old.Children.Remove(_opacity);
        var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = "窗口不透明度" }); _opacity.Width = 160; ApplyStyle(_opacity, "BrightnessSliderStyle"); panel.Children.Add(_opacity);
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) }; border.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush");
        var popup = new Popup { Child = border, PlacementTarget = anchor, Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = false }; popup.IsOpen = true;
    }

    private void RebuildSessionSidebar(IReadOnlyList<AssistantSession> sessions)
    {
        _updatingSidebar = true; _sessionSidebar.Items.Clear();
        foreach (var session in sessions)
        {
            var row = new Grid { Tag = session, Margin = new Thickness(2, 3, 2, 3) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = session.Title });
            var delete = ChatIcon("\uE74D", "删除对话", (_, _) => _ = DeleteSidebarSessionAsync(session)); delete.Width = 28; delete.Height = 28; Grid.SetColumn(delete, 1); row.Children.Add(delete); _sessionSidebar.Items.Add(row);
            if (_session?.Id == session.Id) _sessionSidebar.SelectedItem = row;
        }
        _updatingSidebar = false;
    }

    private async Task DeleteSidebarSessionAsync(AssistantSession session)
    {
        if (!AssistantTextDialog.Confirm(this, "删除对话", "删除“" + session.Title + "”及其派生记忆？已保存的自动任务不受影响。")) return;
        await UiActionAsync(async () => { StopSpeechRequested?.Invoke(); await _runtime.DeleteSessionAsync(session.Id, CancellationToken.None); if (_session?.Id == session.Id) _session = null; await ReloadSessionsAsync(); });
    }

    private void ShowTargetMenu(Button anchor)
    {
        var menu = new ContextMenu(); var auto = new MenuItem { Header = "自动判断对象（执行前确认）", IsCheckable = true, IsChecked = _runtime.TargetEnvironmentId == Guid.Empty };
        auto.Click += (_, _) => { _runtime.SetTarget(Guid.Empty); _targets.SelectedIndex = -1; UpdateTargetCaption(); }; menu.Items.Add(auto);
        foreach (var target in GlobalTaskTarget.All(_settings.Current.DesktopSessions)) { var item = new MenuItem { Header = target.Name, IsCheckable = true, IsChecked = target.Id == _runtime.TargetEnvironmentId }; item.Click += (_, _) => { _targets.SelectedItem = _targets.Items.OfType<EnvironmentProfile>().FirstOrDefault(profile => profile.Id == target.Id); _runtime.SetTarget(target.Id); UpdateTargetCaption(); }; menu.Items.Add(item); }
        menu.PlacementTarget = anchor; anchor.ContextMenu = menu; menu.IsOpen = true;
    }
    private void UpdateTargetCaption() => _targetCaption.Text = _runtime.TargetEnvironmentId == Guid.Empty ? "自动选择对象" : GlobalTaskTarget.Find(_settings.Current.DesktopSessions, _runtime.TargetEnvironmentId)?.Name ?? "对象已移除";
    private async Task ShowSkillMenuAsync(Button anchor)
    {
        await UiActionAsync(async () => { var skills = (await _store.ListAssetsAsync(CancellationToken.None)).Where(asset => asset.Kind == "skill").ToArray(); var menu = new ContextMenu(); foreach (var skill in skills) { var item = new MenuItem { Header = skill.Name, IsCheckable = true, IsChecked = _mentionedSkills.Contains(skill.Id) }; item.Click += (_, _) => { if (!_mentionedSkills.Add(skill.Id)) _mentionedSkills.Remove(skill.Id); _mentionHint.Text = string.Join(" ", skills.Where(asset => _mentionedSkills.Contains(asset.Id)).Select(asset => "@" + asset.Name)); }; menu.Items.Add(item); } if (skills.Length == 0) menu.Items.Add(new MenuItem { Header = "请先在设置中添加Skill", IsEnabled = false }); menu.PlacementTarget = anchor; anchor.ContextMenu = menu; menu.IsOpen = true; });
    }
    private void UpdateMentionHint() => _mentionHint.Text = "";

    private static string SendKeyHint(AssistantSettings settings) => settings.SendMode switch { 1 => "Enter发送", 2 => settings.SendShortcut + "发送", _ => "Ctrl+Enter发送" };
    internal static bool ShouldSend(AssistantSettings settings, Key key, ModifierKeys modifiers, bool composing, Key systemKey = Key.None)
    {
        if (key == Key.System) key = systemKey;
        if (composing || key is Key.ImeProcessed or Key.DeadCharProcessed || key == Key.Enter && modifiers.HasFlag(ModifierKeys.Shift)) return false;
        if (settings.SendMode == 1) return key == Key.Enter && modifiers == ModifierKeys.None;
        if (settings.SendMode == 0) return key == Key.Enter && modifiers == ModifierKeys.Control;
        if (!SettingsViewModel.TryParseAssistantSendKey(settings.SendShortcut, out var chord)) return false;
        var actual = (modifiers.HasFlag(ModifierKeys.Control) ? 2 : 0) | (modifiers.HasFlag(ModifierKeys.Alt) ? 1 : 0) | (modifiers.HasFlag(ModifierKeys.Shift) ? 4 : 0) | (modifiers.HasFlag(ModifierKeys.Windows) ? 8 : 0);
        return chord.VirtualKey == (uint)KeyInterop.VirtualKeyFromKey(key) && ((int)chord.Modifiers & 15) == actual;
    }
    private void InitializeSendKeys()
    {
        TextCompositionManager.AddPreviewTextInputStartHandler(_input, (_, _) => _composing = true); TextCompositionManager.AddPreviewTextInputHandler(_input, (_, _) => _composing = false);
        _input.PreviewKeyDown += async (_, args) => { if (ShouldSend(_settings.Current.Assistant, args.Key, Keyboard.Modifiers, _composing, args.SystemKey) && !args.IsRepeat) { args.Handled = true; await SendMessageAsync(); } };
    }
}
