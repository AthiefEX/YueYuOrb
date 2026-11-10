using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private IAssistantStore? _assistantStore;
    private IAssistantCredentialStore? _assistantCredentials;
    private PasswordBox _assistantKey = null!;
    private PasswordBox _assistantVoiceKey = null!;
    private TextBlock _assistantStatus = null!;
    private StackPanel _assistantAssets = null!;
    private StackPanel _assistantMemories = null!;
    private TabItem _assistantTab = null!;
    private CancellationTokenSource? _assistantTest;
    public void SelectAssistantPage() => SettingsTabControl.SelectedItem = _assistantTab;

    private void InitializeAssistantSettings(IAssistantStore? store, IAssistantCredentialStore? credentials, Action? openAssistant)
    {
        _assistantStore = store; _assistantCredentials = credentials;
        _assistantTab = new TabItem { Header = "AI助手", Style = (Style)FindResource("NavigationTabItemStyle") };
        AutomationProperties.SetName(_assistantTab, "AI助手");
        var categories = CategoryTabs(); categories.Margin = new Thickness(28, 16, 28, 24);
        var connection = new StackPanel(); connection.Children.Add(SectionTitle("AI助手"));
        connection.Children.Add(Description("聊天、电脑操作和持久自动任务相互独立。关闭助手会停止当前对话、录音及临时操作；已启用的持久任务需在自动任务页单独停用。修改后点击底部“应用”。"));
        AssistantToggle(connection, "启用AI助手", "AssistantEnabled");
        _assistantStatus = Description("尚未测试连接。助手不会自动安装本地模型或替换已有网络设置。"); connection.Children.Add(_assistantStatus);
        InitializeAssistantConnectionManager(connection, openAssistant);
        AssistantToggle(connection, "流式显示回复", "AssistantStream");
        AssistantToggle(connection, "接口支持工具调用（不支持时关闭，仍可聊天）", "AssistantSupportsTools");
        AssistantToggle(connection, "明确允许配置的内网接口", "AssistantAllowPrivateEndpoint");
        AddCategory(categories, "连接与模型", connection);

        var assets = new StackPanel(); assets.Children.Add(SectionTitle("角色与Skill"));
        assets.Children.Add(Description("支持SKILL.md及目录内Markdown/文本参考，角色支持Markdown、JSON、Character Card V2 JSON或带chara文本的PNG。导入不执行脚本、不安装依赖、不自动授予电脑权限；启用的素材可能随对话发送至所选API。"));
        assets.Children.Add(ButtonRow(Button("导入Skill", (_, _) => _ = ImportAssistantAssetAsync("skill")), Button("导入角色卡", (_, _) => _ = ImportAssistantAssetAsync("role")), Button("刷新", (_, _) => _ = RefreshAssistantLibraryAsync())));
        assets.Children.Add(ButtonRow(Button("手动添加Skill", (_, _) => _ = AssistantActionAsync(() => EditAssistantAssetAsync("skill"))), Button("手动添加角色卡", (_, _) => _ = AssistantActionAsync(() => EditAssistantAssetAsync("role")))));
        _assistantAssets = new StackPanel(); assets.Children.Add(_assistantAssets);
        AssistantInput(assets, "助手基础提示词（角色/Skill不能覆盖操作授权）", "AssistantSystemPrompt", multiline: true);
        AddCategory(categories, "角色与Skill", assets);

        var permissions = new StackPanel(); permissions.Children.Add(SectionTitle("操作与自动任务"));
        permissions.Children.Add(Description("操作只作用于助手窗口选中的目标。首次控制需确认本次会话权限；持久任务保存/启用展示完整步骤，不借用外部MCP信任。没有真实执行回执时不显示成功。"));
        AssistantToggle(permissions, "允许请求控制电脑（仍需本次会话确认）", "AssistantAllowComputerControl");
        AssistantToggle(permissions, "允许生成和管理自动任务（保存/启用前确认）", "AssistantAllowTaskManagement");
        AssistantToggle(permissions, "允许AI创建/编辑角色卡和Skill（完整预览后确认保存）", "AssistantAllowAssetEditing");
        permissions.Children.Add(Description("助手关闭或没有API连接后，已确认启用的确定性任务仍可独立运行。可在“自动任务”页修改、停用、手工导入或运行。脚本必须作为任务资产单独审核，不能从角色卡直接执行。"));
        AddCategory(categories, "操作与任务", permissions);

        var memory = new StackPanel(); memory.Children.Add(SectionTitle("记忆与隐私"));
        AssistantToggle(memory, "保存聊天记录到本机", "AssistantSaveHistory");
        AssistantToggle(memory, "按需检索历史聊天（本机检索，云端只发送相关片段并确认）", "AssistantSearchHistory");
        AssistantToggle(memory, "在对话中使用已启用记忆（云端外发按次确认）", "AssistantUseMemory");
        memory.Children.Add(Description("基础记忆由用户明确保存，保留来源与修订。删除会清理历史并抑制该来源再次提取；不自动把全部聊天变为事实，不默认上传屏幕、文件或私密记忆。密钥不进入聊天上下文。"));
        memory.Children.Add(ButtonRow(Button("手动添加记忆", (_, _) => _ = AddAssistantMemoryAsync()), Button("刷新", (_, _) => _ = RefreshAssistantLibraryAsync())));
        _assistantMemories = new StackPanel(); memory.Children.Add(_assistantMemories); AddCategory(categories, "记忆与隐私", memory);
        InitializeAssistantKnowledgeSettings(categories, memory);

        var voice = new StackPanel(); voice.Children.Add(SectionTitle("可选语音"));
        AssistantToggle(voice, "启用按键录音和语音识别", "AssistantVoiceEnabled");
        AssistantInput(voice, "语音识别API地址（兼容audio/transcriptions，可独立于聊天）", "AssistantVoiceEndpoint");
        AssistantInput(voice, "语音识别模型", "AssistantVoiceModel");
        voice.Children.Add(Description("录音只在主动点击后开始，默认识别结果先填入输入框。语音发送到配置的识别接口，不假设聊天API自带语音，也不保证免费；无设备或接口时仍可文字聊天。"));
        _assistantVoiceKey = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(10), MaxLength = 8192 }; _assistantVoiceKey.SetResourceReference(StyleProperty, "SettingsPasswordBoxStyle"); voice.Children.Add(_assistantVoiceKey);
        voice.Children.Add(ButtonRow(Button("保存语音密钥", (_, _) => _ = AssistantActionAsync(() => SaveAssistantKeyAsync(true))), Button("删除语音密钥", (_, _) => _ = AssistantActionAsync(async () => { if (_assistantCredentials is not null) await _assistantCredentials.DeleteAsync("asr", _viewModel.AssistantVoiceEndpoint.Trim(), CancellationToken.None); _assistantStatus.Text = "语音密钥已删除。"; }))));
        AssistantToggle(voice, "识别后自动发送（关闭则先编辑文字）", "AssistantVoiceAutoSend");
        AssistantToggle(voice, "使用系统可用声音朗读回复", "AssistantReadReplies"); AddCategory(categories, "语音", voice);

        var appearance = new StackPanel(); appearance.Children.Add(SectionTitle("窗口与入口"));
        AssistantInput(appearance, "助手窗口标题", "AssistantWindowTitle");
        AssistantInput(appearance, "对话中的助手名称", "AssistantDisplayName");
        AssistantInput(appearance, "对话中的用户名称", "AssistantUserDisplayName");
        AssistantToggle(appearance, "在轮盘中显示AI助手", "AssistantShowInWheel");
        AssistantInput(appearance, "呼出快捷键（默认留空，不抢占已有组合）", "AssistantHotkey");
        AssistantToggle(appearance, "助手窗口置顶", "AssistantAlwaysOnTop");
        AssistantSlider(appearance, "窗口不透明度（20%–100%，降低后可透出后方窗口）", "AssistantOpacityPercent", 20, 100);
        AssistantToggle(appearance, "启用助手独立图片背景", "AssistantBackgroundEnabled");
        AssistantSlider(appearance, "背景图片不透明度（不改变文字清晰度）", "AssistantBackgroundOpacityPercent", 0, 100);
        appearance.Children.Add(ButtonRow(Button("选择并裁切背景", (_, _) => _ = ChooseAssistantBackgroundAsync()), Button("关闭图片背景", (_, _) => { _viewModel.AssistantBackgroundEnabled = false; })));
        appearance.Children.Add(Description("助手使用独立背景，不替换设置页或浏览器背景。真正独占全屏游戏可能覆盖普通置顶窗口。")); AddCategory(categories, "窗口与入口", appearance);
        appearance.Children.Add(SectionTitle("消息发送方式"));
        var sendMode = new ComboBox { ItemsSource = new[] { "Ctrl+Enter发送", "Enter发送（Shift+Enter换行）", "自定义按键发送" }, Margin = new Thickness(0, 8, 0, 8) }; sendMode.SetResourceReference(StyleProperty, "SettingsComboBoxStyle"); sendMode.SetBinding(ComboBox.SelectedIndexProperty, new Binding("AssistantSendMode") { Mode = BindingMode.TwoWay }); appearance.Children.Add(sendMode);
        AssistantInput(appearance, "自定义发送键（仅输入框内生效，如 Alt+Enter / F8）", "AssistantSendShortcut");
        _assistantTab.Content = categories; SettingsTabControl.Items.Insert(Math.Max(0, SettingsTabControl.Items.Count - 1), _assistantTab);
        Loaded += async (_, _) => await RefreshAssistantLibraryAsync();
        Closing += (_, _) => { _assistantTest?.Cancel(); _assistantKey.Clear(); _assistantVoiceKey.Clear(); };
    }

    private void AssistantToggle(Panel panel, string label, string property)
    {
        var control = new CheckBox { Content = label, Margin = new Thickness(0, 10, 0, 8), Style = (Style)FindResource("ToggleSwitchStyle") };
        control.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(control);
    }

    private static void AssistantInput(Panel panel, string label, string property, bool multiline = false)
    {
        panel.Children.Add(Description(label)); var control = new TextBox { Margin = new Thickness(0, 6, 0, 10), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 110 : 36, MaxLength = multiline ? 16000 : 2048 };
        control.SetResourceReference(StyleProperty, "SettingsTextBoxStyle"); control.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); panel.Children.Add(control);
    }

    private static void AssistantSlider(Panel panel, string label, string property, double minimum, double maximum)
    {
        panel.Children.Add(Description(label)); var slider = new Slider { Minimum = minimum, Maximum = maximum, Margin = new Thickness(0, 6, 0, 12), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Width = 350 };
        slider.SetResourceReference(StyleProperty, "SettingsSliderStyle"); slider.SetBinding(Slider.ValueProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(slider);
    }

    private async Task AssistantActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { _assistantStatus.Text = "助手操作已取消。"; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        { _assistantStatus.Text = error is InvalidOperationException ? error.Message : $"助手操作未完成（{error.GetType().Name}），请检查设置后重试。"; }
    }

    private async Task SaveAssistantKeyAsync(bool voice)
    {
        if (_assistantCredentials is null) { _assistantStatus.Text = "此窗口未连接助手凭据服务。"; return; }
        var box = voice ? _assistantVoiceKey : _assistantKey;
        await _assistantCredentials.SaveAsync(voice ? "asr" : "chat", (voice ? _viewModel.AssistantVoiceEndpoint : _viewModel.AssistantEndpoint).Trim(), box.Password, CancellationToken.None);
        box.Clear(); _assistantStatus.Text = "密钥已加密保存；如更改了接口或模型，请点击应用。";
    }

    private async Task TestAssistantConnectionAsync()
    {
        _assistantTest?.Cancel(); _assistantTest = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = _assistantTest.Token;
        var settings = _viewModel.BuildCandidate(_settings.Current).Assistant with { Enabled = true, SupportsTools = false, TimeoutSeconds = 15, MaxResponseCharacters = 1000 };
        if (AssistantSettingsValidator.Validate(settings).Count != 0) throw new InvalidOperationException("请先填写有效接口和模型。");
        var key = _assistantCredentials is null ? null : await _assistantCredentials.GetAsync("chat", settings.Endpoint, token);
        using var http = AssistantApiClient.CreateHttpClient(); var client = new AssistantApiClient(http); var replied = false;
        _assistantStatus.Text = "正在测试连接…";
        await foreach (var delta in client.StreamAsync(new(settings, [new(Guid.NewGuid(), Guid.NewGuid(), "user", "请仅回复OK，用于测试连接。", DateTimeOffset.UtcNow)], key), token)) replied |= delta.Text.Length > 0;
        _assistantStatus.Text = replied ? "连接已通过实际请求；工具和语音能力需分别验证。" : "接口没有返回正文，请检查模型。";
    }

    private async Task ImportAssistantAssetAsync(string kind)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = kind == "skill" ? "Skill入口|SKILL.md" : "角色卡|*.md;*.txt;*.json;*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await AssistantActionAsync(async () =>
        {
            if (_assistantStore is null) throw new InvalidOperationException("助手素材服务未初始化。");
            var asset = await AssistantAssetLoader.LoadAsync(dialog.FileName, kind, CancellationToken.None);
            await CheckAssistantStoredSecretsAsync(asset.Text);
            await _assistantStore.SaveAssetAsync(asset, CancellationToken.None); await RefreshAssistantLibraryAsync();
            _assistantStatus.Text = "素材已导入，默认未启用。";
        });
    }

    private async Task RefreshAssistantLibraryAsync()
    {
        if (_assistantStore is null) return;
        await AssistantActionAsync(async () =>
        {
            var assets = await _assistantStore.ListAssetsAsync(CancellationToken.None); var memories = await _assistantStore.ListMemoriesAsync(CancellationToken.None);
            _assistantAssets.Children.Clear(); _assistantMemories.Children.Clear();
            foreach (var asset in assets)
            {
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                var actions = ButtonRow(Button("预览", (_, _) => AssistantTextDialog.Show(this, asset.Name, asset.Text, editable: false)), Button("移除", (_, _) => _ = AssistantActionAsync(async () =>
                { if (!AssistantTextDialog.Confirm(this, "移除素材", asset.Name)) return; await _assistantStore.DeleteAssetAsync(asset.Id, CancellationToken.None); await _settings.UpdateAsync(current => current with { Assistant = current.Assistant with { EnabledAssetIds = current.Assistant.EnabledAssetIds.Where(id => id != asset.Id).ToArray() } }, CancellationToken.None); await RefreshAssistantLibraryAsync(); })));
                DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
                var toggle = new CheckBox { IsChecked = _settings.Current.Assistant.EnabledAssetIds.Contains(asset.Id), VerticalAlignment = VerticalAlignment.Center, ToolTip = "启用/关闭 " + asset.Name, Style = (Style)FindResource("ToggleSwitchStyle"), Width = 50 };
                toggle.Click += async (_, _) => await AssistantActionAsync(async () => { var enabled = toggle.IsChecked == true; var result = await _settings.UpdateAsync(current => current with { Assistant = current.Assistant with { EnabledAssetIds = AssistantWorkspacePolicy.SetAsset(current.Assistant, assets, asset.Id, enabled) } }, CancellationToken.None); if (!result.Success) _assistantStatus.Text = "素材启用状态未保存。"; await RefreshAssistantLibraryAsync(); });
                actions.Children.Insert(0, toggle); actions.Children.Insert(1, Button("编辑", (_, _) => _ = AssistantActionAsync(() => EditAssistantAssetAsync(asset.Kind, asset))));
                row.Children.Add(new TextBlock { Text = $"{asset.Name} · {asset.Kind}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = asset.SourcePath }); _assistantAssets.Children.Add(row);
            }
            foreach (var memory in memories)
            {
                _assistantMemories.Children.Add(CreateAssistantMemoryRow(memory));
            }
            if (assets.Count == 0) _assistantAssets.Children.Add(Description("尚未导入角色或Skill。"));
            if (memories.Count == 0) _assistantMemories.Children.Add(Description("尚无已保存记忆。"));
        });
    }

    private async Task AddAssistantMemoryAsync()
    {
        await AssistantActionAsync(async () => { if (_assistantStore is not IAssistantKnowledgeStore knowledge) return;
            var memory = AssistantMemoryEditor.Edit(this, new(Guid.NewGuid(), "", null, 1, true, DateTimeOffset.UtcNow), await knowledge.ListProjectsAsync(CancellationToken.None)); if (memory is null) return;
            await CheckAssistantStoredSecretsAsync(memory.Content + "\n" + memory.Key); await _assistantStore.SaveMemoryAsync(memory, CancellationToken.None); await RefreshAssistantLibraryAsync(); });
    }

    private async Task CheckAssistantStoredSecretsAsync(string text)
    {
        if (_assistantCredentials is not null) await AssistantPrivacy.CheckStoredSecretsAsync(text, _viewModel.BuildCandidate(_settings.Current).Assistant, _assistantCredentials, CancellationToken.None);
        else if (AssistantPrivacy.SecretReason(text) is { } reason) throw new InvalidOperationException(reason);
    }

    private async Task ChooseAssistantBackgroundAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true }; if (dialog.ShowDialog(this) != true) return;
        await AssistantActionAsync(async () =>
        {
            var crop = new BackgroundCropWindow(dialog.FileName) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }; if (crop.ShowDialog() != true) return;
            crop.Save(Path.Combine(_paths.Root, "assistant", "background.png")); _viewModel.AssistantBackgroundEnabled = true;
            var result = await _settings.UpdateAsync(current => current with { Assistant = current.Assistant with { Window = current.Assistant.Window with { BackgroundEnabled = true, BackgroundOpacity = _viewModel.AssistantBackgroundOpacityPercent / 100 } } }, CancellationToken.None);
            _assistantStatus.Text = result.Success ? "助手背景已保存。" : "图片已保存，请点击应用重试。";
        });
    }
}
