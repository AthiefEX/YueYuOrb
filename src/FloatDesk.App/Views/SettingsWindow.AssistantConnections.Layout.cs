using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private StackPanel _assistantConnectionEditor = null!;
    private bool _assistantBatchTesting;

    private void InitializeCompactConnections(Panel panel, Action? openAssistant)
    {
        _assistantProfiles = new ComboBox { DisplayMemberPath = "Name" };
        _assistantProfiles.SelectionChanged += (_, _) => { if (!_refreshingAssistantProfiles && _assistantProfiles.SelectedItem is AssistantApiProfile profile) ShowAssistantProfile(profile); };
        _assistantConnectionEditor = new StackPanel();
        _assistantProfileName = ProfileInput(_assistantConnectionEditor, "密钥名称");
        _assistantProfileEndpoint = ProfileInput(_assistantConnectionEditor, "兼容API地址（例如 https://服务地址/v1）");
        _assistantConnectionEditor.Children.Add(Description("API密钥（已有连接留空保留，密钥不回显）"));
        _assistantKey = new PasswordBox { MaxLength = 8192, Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 12) }; _assistantKey.SetResourceReference(StyleProperty, "SettingsPasswordBoxStyle"); _assistantConnectionEditor.Children.Add(_assistantKey);
        _assistantProfileEnabled = new CheckBox { Content = "启用密钥", IsChecked = true }; _assistantProfileEnabled.SetResourceReference(StyleProperty, "ToggleSwitchStyle"); _assistantConnectionEditor.Children.Add(_assistantProfileEnabled);
        _assistantModels = new ComboBox();
        _assistantManualModel = new TextBox { MaxLength = 200, Margin = new Thickness(0, 6, 0, 10), ToolTip = "选择或输入模型ID，再点击用于聊天或用于搜索" }; _assistantManualModel.SetResourceReference(StyleProperty, "SettingsTextBoxStyle");
        _assistantManualModel.TextChanged += (_, _) => { _assistantModels.Text = _assistantManualModel.Text; _assistantDiscoveryVersion++; UpdateConnectionRoleSwitches(); };
        _assistantModels.SelectionChanged += (_, _) => { if (_assistantModels.SelectedItem is string model) _assistantManualModel.Text = model; };
        _assistantKey.PasswordChanged += (_, _) => _assistantDiscoveryVersion++;
        _assistantProfileEndpoint.TextChanged += (_, _) => { _assistantDiscoveryVersion++; _assistantModels.ItemsSource = null; };
        panel.Children.Add(Description("模型开关控制此密钥的可用状态。只允许一个联网搜索模型；服务本身须支持联网。选择和开关立即保存，不自动轮换密钥。"));
        var columns = new Grid { Margin = new Thickness(0, 8, 0, 8) }; columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1.3, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; left.Children.Add(SectionTitle("已保存密钥"));
        left.Children.Add(ButtonRow(Button("新增密钥", (_, _) => OpenAssistantConnectionEditor(true)), Button("编辑", (_, _) => OpenAssistantConnectionEditor(false)), Button("删除", (_, _) => _ = AssistantActionAsync(DeleteAssistantProfileAsync))));
        var headings = new Grid(); headings.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var label in new[] { "名称", "模型", "搜索", "刷新" }) { var column = headings.Children.Count; if (column > 0) headings.ColumnDefinitions.Add(new() { Width = new GridLength(54) }); var heading = Description(label); heading.HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center; Grid.SetColumn(heading, column); headings.Children.Add(heading); } left.Children.Add(headings);
        _assistantProfileRows = new ListBox { MinHeight = 160, MaxHeight = 290, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch }; _assistantProfileRows.SetResourceReference(ForegroundProperty, "SettingsInkBrush"); left.Children.Add(_assistantProfileRows);
        _assistantProfileRows.SelectionChanged += (_, _) => { if (!_assistantRoleUpdating && _assistantProfileRows.SelectedItem is Grid { Tag: AssistantApiProfile profile }) _ = AssistantActionAsync(async () => { ShowAssistantProfile(profile); if (profile.Enabled && !_assistantDiscoveredModels.ContainsKey((profile.Id, profile.Endpoint))) await DiscoverAssistantModelsAsync(profile); }); };
        var right = new StackPanel(); right.Children.Add(SectionTitle("所选密钥可用模型"));
        _assistantAvailableModels = new ListBox { MinHeight = 160, MaxHeight = 230, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent }; _assistantAvailableModels.SetResourceReference(ForegroundProperty, "SettingsInkBrush");
        _assistantAvailableModels.ItemTemplate = ConnectionModelTemplate();
        _assistantProfileRows.ItemContainerStyle = AssistantListStyle.Create(); _assistantAvailableModels.ItemContainerStyle = AssistantListStyle.Create();
        _assistantAvailableModels.SelectionChanged += (_, _) => { if (!_assistantRoleUpdating && _assistantAvailableModels.SelectedItem is ConnectionModelRow model) _assistantManualModel.Text = model.Id; };
        var border = new Border { Child = _assistantAvailableModels, Padding = new Thickness(6), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) }; border.SetResourceReference(Border.BackgroundProperty, "SettingsFieldBrush"); border.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush"); right.Children.Add(border);
        right.Children.Add(Description("当前选择模型（也可手动输入ID）")); right.Children.Add(_assistantManualModel);
        _assistantUseChat = new CheckBox { Content = "用于聊天", Margin = new Thickness(0, 3, 0, 3) }; _assistantUseChat.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
        _assistantUseChat.Click += (_, _) => _ = AssistantActionAsync(async () => { if (_assistantUseChat.IsChecked == true) await UseAssistantProfileAsync(false); else { await CommitAssistantProfilesAsync(current => current with { ChatModelEnabled = false }); RefreshConnectionModels(); } });
        _assistantUseSearch = new CheckBox { Content = "用于联网搜索（唯一）", Margin = new Thickness(0, 3, 0, 3) }; _assistantUseSearch.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
        _assistantUseSearch.Click += (_, _) => { if (_editingAssistantProfile is { } profile) _ = ChangeSearchRoleAsync(profile, _assistantManualModel.Text.Trim(), _assistantUseSearch.IsChecked == true); };
        right.Children.Add(_assistantUseChat); right.Children.Add(_assistantUseSearch);
        right.Children.Add(ButtonRow(Button("检测所选", (_, _) => _ = AssistantActionAsync(TestAssistantProfileModelAsync)), Button("全部检测", (_, _) => _ = AssistantActionAsync(() => TestAssistantModelsAsync(true))), Button("停止检测", (_, _) => _assistantTest?.Cancel())));
        _assistantModelResult = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = "点击检测后显示每个模型的实际回复或失败状态。全部检测会逐个发起请求，可能计费。" }; _assistantModelResult.SetResourceReference(StyleProperty, "SettingsTextBoxStyle"); right.Children.Add(_assistantModelResult);
        columns.Children.Add(left); Grid.SetColumn(right, 1); columns.Children.Add(right); panel.Children.Add(columns);
        _assistantProfileSummary = Description(""); panel.Children.Add(_assistantProfileSummary);
        panel.Children.Add(Button("打开助手", (_, _) => openAssistant?.Invoke())); RefreshAssistantProfiles();
    }

    private void OpenAssistantConnectionEditor(bool create)
    {
        if (!create && _editingAssistantProfile is null) { _assistantStatus.Text = "请先选择密钥。"; return; }
        var previous = _editingAssistantProfile; _assistantTest?.Cancel();
        if (create) { _editingAssistantProfile = null; _assistantProfileName.Text = "新密钥"; _assistantProfileEndpoint.Text = ""; _assistantKey.Clear(); _assistantProfileEnabled.IsChecked = true; }
        var modal = new Window { Owner = this, Title = create ? "新增密钥" : "编辑密钥", Width = 640, Height = 560, MinWidth = 440, MinHeight = 400, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        modal.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) }); modal.SetResourceReference(ForegroundProperty, "SettingsInkBrush");
        var root = new DockPanel { Margin = new Thickness(20) }; var scroll = new ScrollViewer { Content = _assistantConnectionEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var model = new TextBox { Text = create ? "" : _assistantManualModel.Text, MaxLength = 200, Margin = new Thickness(0, 8, 0, 8) }; model.SetResourceReference(StyleProperty, "SettingsTextBoxStyle");
        var extra = new StackPanel(); extra.Children.Add(Description("初始模型ID（可留空，保存后读取模型列表）")); extra.Children.Add(model); _assistantConnectionEditor.Children.Add(extra);
        var result = Description(""); extra.Children.Add(result); var saved = false; var saving = false;
        modal.Closing += (_, args) => { if (saving) args.Cancel = true; };
        var save = Button("保存并读取模型", async (_, _) =>
        {
            if (saved || saving) return;
            saving = true;
            try { _assistantManualModel.Text = model.Text.Trim(); await SaveAssistantProfileAsync(); saved = true; saving = false; modal.DialogResult = true; }
            catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { result.Text = "保存失败，请检查名称、地址、模型与密钥。"; }
            finally { saving = false; }
        });
        var actions = ButtonRow(save, Button("取消", (_, _) => modal.DialogResult = false)); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions); root.Children.Add(scroll); modal.Content = root; GlassWindowFrame.WrapStandardWindow(modal);
        try { modal.ShowDialog(); }
        finally { scroll.Content = null; _assistantConnectionEditor.Children.Remove(extra); _assistantKey.Clear(); }
        if (!saved) { _editingAssistantProfile = previous; if (previous is not null) ShowAssistantProfile(previous); }
        else if (_editingAssistantProfile is { Enabled: true } profile) _ = AssistantActionAsync(() => DiscoverAssistantModelsAsync(profile));
    }

    private async Task TestAssistantModelsAsync(bool all)
    {
        if (_assistantBatchTesting) { _assistantStatus.Text = "正在检测，可先停止再开始。"; return; }
        if (_editingAssistantProfile is not { Enabled: true, ModelEnabled: true } profile) throw new InvalidOperationException("请先启用所选密钥与模型。");
        var models = all ? _assistantAvailableModels?.Items.OfType<ConnectionModelRow>().Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray() ?? [] : new[] { _assistantManualModel.Text.Trim() };
        if (models.Length == 0 || models.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("请先刷新模型列表或填写模型ID。");
        if (all && !AssistantTextDialog.Confirm(this, "全部检测模型", $"将串行向此连接的{models.Length}个模型发送短测试请求，服务可能按次计费。可随时停止；不会更改当前聊天或搜索模型。继续？")) return;
        _assistantTest?.Cancel(); _assistantTest = new CancellationTokenSource(); var token = _assistantTest.Token; var version = _assistantDiscoveryVersion; _assistantBatchTesting = true;
        _assistantModelResult!.Text = profile.Name + " · 检测结果\n";
        try
        {
            var key = await _assistantCredentials!.GetAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, token);
            using var http = AssistantApiClient.CreateHttpClient(); var catalog = new AssistantModelCatalog(http);
            foreach (var model in models)
            {
                token.ThrowIfCancellationRequested(); if (version != _assistantDiscoveryVersion) return;
                _assistantStatus.Text = $"正在检测 {model}…"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                string reply;
                try { reply = "通过 · " + await catalog.TestAsync(_viewModel.BuildCandidate(_settings.Current).Assistant with { Endpoint = profile.Endpoint, Model = model }, key, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { reply = "未通过 · 请求超时"; }
                catch (Exception error) when (error is InvalidOperationException or IOException or System.Net.Http.HttpRequestException) { reply = "未通过 · " + error.GetType().Name + "（请检查模型权限、接口和额度）"; }
                if (version != _assistantDiscoveryVersion || token.IsCancellationRequested) return;
                _assistantModelHealth[(profile.Id, profile.Endpoint, model)] = reply.StartsWith("通过", StringComparison.Ordinal) ? "连接正常（最近检测）" : "连接失败（最近检测）";
                _assistantModelResult.AppendText(model + "：" + reply + "\n"); RefreshConnectionModels();
            }
            _assistantStatus.Text = "检测完成；结果只代表本次聊天请求，不代表联网或工具能力。";
        }
        catch (OperationCanceledException) { if (version == _assistantDiscoveryVersion) _assistantStatus.Text = "检测已停止，已完成结果保留。"; }
        finally { _assistantBatchTesting = false; }
    }
}
