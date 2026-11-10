using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using FloatDesk.App.Assistant;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public sealed partial class AssistantWindow : Window, IDisposable
{
    private readonly ISettingsCoordinator _settings;
    private readonly IAssistantStore _store;
    private readonly AssistantRuntime _runtime;
    private readonly AppDataPaths _paths;
    private readonly ComboBox _sessions = new() { Width = 180, DisplayMemberPath = "Title", Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _targets = new() { Width = 205, DisplayMemberPath = "Name", Margin = new Thickness(0, 0, 6, 0) };
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 66, MaxHeight = 180, MaxLength = 32000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock _connection = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Dictionary<Guid, TextBox> _messageViews = [];
    private readonly Dictionary<Guid, (string Role, TextBlock Header)> _messageHeaders = [];
    private readonly Dictionary<Guid, WrapPanel> _messageSources = [];
    private readonly Dictionary<Guid, string> _pendingText = [];
    private Button _send = null!;
    private Button _record = null!;
    private readonly Slider _opacity = new() { Minimum = 20, Maximum = 100, Width = 100, Value = 100, ToolTip = "窗口不透明度", Margin = new Thickness(5, 0, 0, 0) };
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AssistantSession? _session;
    private bool _applying;
    private bool _positionRestored;
    private bool _disposed;
    private int _loadVersion;
    private Grid _surface = null!;
    public event Action? VoiceRequested;
    public event Action? StopSpeechRequested;
    public event Action<string>? ReplyCompleted;
    public TextBox MessageInput => _input;
    public Slider WindowOpacitySlider => _opacity;
    public Guid? CurrentSessionId => _session?.Id;

    public AssistantWindow(AppDataPaths paths, ISettingsCoordinator settings, IAssistantStore store, AssistantRuntime runtime, Action openSettings)
    {
        _paths = paths; _settings = settings; _store = store; _runtime = runtime;
        Title = "月语 AI助手"; Width = 720; Height = 780; MinWidth = 480; MinHeight = 420;
        SetResourceReference(ForegroundProperty, "SettingsInkBrush");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new() { CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/BrightnessControlResources.xaml", UriKind.Relative) });
        InitializeChatWorkspace(openSettings);
        Content = _surface; GlassWindowFrame.WrapStandardWindow(this);
        _runtime.MessageAdded += OnMessageAdded; _runtime.TextChanged += OnTextChanged; _runtime.StatusChanged += OnStatusChanged;
        _runtime.ConfirmOutbound = (request, token) => Dispatcher.InvokeAsync(() => AssistantTextDialog.Confirm(this, "确认本次外发", $"目标：{new Uri(request.Settings.Endpoint).Host}\n模型：{request.Settings.Model}\n本次涉及独立搜索接收方或附加上下文。以下是实际外发的消息和工具内容：\n\n" + System.Text.Encoding.UTF8.GetString(AssistantApiClient.CreatePayloadBytes(request)), token)).Task;
        _runtime.ConfirmTool = (preview, token) => Dispatcher.InvokeAsync(() => AssistantTextDialog.Confirm(this, preview.Category == "assets" ? "审核角色/Skill修改" : preview.AlwaysConfirm ? "审核并确认操作" : "允许本次会话操作此目标", preview.Title + "\n" + (preview.Category == "assets" ? "仅保存正文；不会执行脚本或自动授予权限。\n" : preview.Category == "tasks" ? "保存/启用持久任务后，助手关闭仍可按计划运行；启用Manual任务可能立即执行。\n" : "操作目标和内容见下方；关闭权限或切换对象将清除会话授权。\n") + preview.Description, token)).Task;
        Loaded += async (_, _) => await ReloadSessionsAsync();
        _sessions.SelectionChanged += async (_, _) => { if (!_applying && _sessions.SelectedItem is AssistantSession selected && selected.Id != _session?.Id) { StopSpeechRequested?.Invoke(); _runtime.Cancel(); _runtime.RevokePermissions(); _session = selected; await LoadCurrentMessagesAsync(); } };
        _targets.SelectionChanged += (_, _) => { if (!_applying && _targets.SelectedItem is EnvironmentProfile target) { StopSpeechRequested?.Invoke(); _runtime.SetTarget(target.Id); } };
        InitializeSendKeys();
        _opacity.ValueChanged += (_, e) => { if (_applying) return; Opacity = e.NewValue / 100; ScheduleSave(); };
        LocationChanged += (_, _) => ScheduleSave(); SizeChanged += (_, _) => ScheduleSave();
        _render.Tick += (_, _) => FlushText(); _render.Start();
        _save.Tick += async (_, _) => { _save.Stop(); if (_disposed || WindowState != WindowState.Normal) return; var bounds = new BrowserWindowBounds(Left, Top, ActualWidth, ActualHeight); var opacity = Opacity; await UiActionAsync(async () => { var result = await _settings.UpdateAsync(current => current with { Assistant = current.Assistant with { Window = current.Assistant.Window with { Bounds = bounds, Opacity = opacity } } }, CancellationToken.None); if (!result.Success) _status.Text = "窗口设置未保存。"; }); };
        Closing += (_, e) => { _runtime.Cancel(); StopSpeechRequested?.Invoke(); _save.Stop(); if (!_disposed) { e.Cancel = true; Hide(); } };
        ApplySettings(settings.Current);
    }

    private static void ApplyStyle(FrameworkElement element, string name) => element.SetResourceReference(StyleProperty, name);
    private static Button ActionButton(string text, RoutedEventHandler action)
    { var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6) }; ApplyStyle(button, "ToolbarButtonStyle"); button.Click += action; return button; }

    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return; _applying = true;
        try
        {
            var value = settings.Assistant.Window; Topmost = value.AlwaysOnTop; Opacity = value.Opacity; _opacity.Value = value.Opacity * 100;
            Title = settings.Assistant.WindowTitle;
            foreach (var (_, header) in _messageHeaders) header.Header.Text = RoleName(header.Role);
            if (!_positionRestored && value.Bounds is { } bounds)
            {
                Width = Math.Clamp(bounds.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth)); Height = Math.Clamp(bounds.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
                Left = Math.Clamp(bounds.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
                Top = Math.Clamp(bounds.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height)); WindowStartupLocation = WindowStartupLocation.Manual;
            }
            _positionRestored = true; var selected = _runtime.TargetEnvironmentId; _targets.ItemsSource = GlobalTaskTarget.All(settings.DesktopSessions).ToArray();
            _targets.SelectedItem = ((IEnumerable<EnvironmentProfile>)_targets.ItemsSource).FirstOrDefault(item => item.Id == selected);
            _connection.Text = $"{(settings.Assistant.Enabled ? settings.Assistant.Model : "助手已关闭")} · {(AssistantEndpointPolicy.IsValid(settings.Assistant.Endpoint) ? new Uri(settings.Assistant.Endpoint).Host : "未配置接口")} · {SendKeyHint(settings.Assistant)}";
            _send.IsEnabled = CanSend() && !_runtime.IsBusy; _record.IsEnabled = settings.Assistant.Enabled && settings.Assistant.Voice.Enabled;
            try { var effective = AssistantConnections.Chat(settings.Assistant); _connection.Text = $"{(settings.Assistant.Enabled ? effective.Model : "助手已关闭")} · {(AssistantEndpointPolicy.IsValid(effective.Endpoint) ? new Uri(effective.Endpoint).Host : "未配置接口")} · {SendKeyHint(settings.Assistant)}"; }
            catch (InvalidOperationException error) { _connection.Text = error.Message; _send.IsEnabled = false; }
            _input.ToolTip = "输入消息；" + SendKeyHint(settings.Assistant); UpdateTargetCaption(); ApplyBackground(settings);
            if (!settings.Assistant.Enabled) { _runtime.Cancel(); StopSpeechRequested?.Invoke(); _status.Text = "助手已关闭；已保存的持久任务仍由自动任务功能管理。"; }
        }
        finally { _applying = false; }
    }

    private void ApplyBackground(AppSettings settings)
    {
        var dark = settings.General.Theme == AppTheme.Dark || settings.General.Theme == AppTheme.System && ThemeService.SystemUsesDarkTheme();
        var color = (Color)ColorConverter.ConvertFromString(dark ? "#121922" : "#F8FBFF"); var baseBrush = new SolidColorBrush(color); _surface.Background = baseBrush;
        if (SystemParameters.HighContrast) { _surface.Background = SystemColors.WindowBrush; return; }
        var path = Path.Combine(_paths.Root, "assistant", "background.png"); if (!settings.Assistant.Window.BackgroundEnabled || !File.Exists(path)) return;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bitmap.UriSource = new Uri(path); bitmap.DecodePixelWidth = 2048; bitmap.EndInit(); bitmap.Freeze();
            var bounds = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight); var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(bounds)));
            var image = new DrawingGroup { Opacity = settings.Assistant.Window.BackgroundOpacity }; image.Children.Add(new ImageDrawing(bitmap, bounds)); drawing.Children.Add(image);
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill }; brush.Freeze(); _surface.Background = brush;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException or ArgumentException) { _status.Text = "背景图片无法读取，已使用原主题。"; }
    }

    private async Task ReloadSessionsAsync()
    {
        await UiActionAsync(async () =>
        {
            var sessions = (await _store.ListSessionsAsync(CancellationToken.None)).ToList();
            if (_session is not null && sessions.All(item => item.Id != _session.Id)) sessions.Insert(0, _session);
            if (_session is null) _session = sessions.FirstOrDefault() ?? await _runtime.CreateSessionAsync("对话 " + DateTime.Now.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture), CancellationToken.None);
            if (sessions.All(item => item.Id != _session.Id)) sessions.Insert(0, _session);
            _applying = true; _sessions.ItemsSource = sessions; _sessions.SelectedItem = _session; _applying = false; RebuildSessionSidebar(sessions); await LoadCurrentMessagesAsync();
        });
    }

    private async Task NewSessionAsync()
    { await UiActionAsync(async () => { StopSpeechRequested?.Invoke(); _runtime.Cancel(); _runtime.RevokePermissions(); _session = await _runtime.CreateSessionAsync("对话 " + DateTime.Now.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture), CancellationToken.None); await ReloadSessionsAsync(); }); }
    private async Task DeleteSessionAsync()
    { if (_session is null || !AssistantTextDialog.Confirm(this, "删除此对话", "删除聊天记录和由这些消息派生的记忆。已保存的自动任务不受影响。")) return; StopSpeechRequested?.Invoke(); await UiActionAsync(async () => { await _runtime.DeleteSessionAsync(_session.Id, CancellationToken.None); _session = null; await ReloadSessionsAsync(); }); }
    private async Task LoadCurrentMessagesAsync()
    {
        if (_session is null) return; var version = ++_loadVersion; var id = _session.Id; _assistantReferenceBar.Children.Clear();
        await UiActionAsync(async () => { var messages = await _runtime.LoadMessagesAsync(id, CancellationToken.None); if (version != _loadVersion || _session?.Id != id) return; _messages.Children.Clear(); _messageViews.Clear(); _messageHeaders.Clear(); _messageSources.Clear(); _pendingText.Clear(); foreach (var message in messages) AddMessageView(message); _scroll.ScrollToEnd(); await LoadAssistantProjectAsync(id); });
    }

    private async Task SendMessageAsync()
    {
        if (_runtime.IsBusy) return; var text = _input.Text; if (string.IsNullOrWhiteSpace(text)) return;
        await UiActionAsync(async () =>
        {
            if (_session is null) await NewSessionAsync(); if (_session is null) return;
            if (_runtime.TargetEnvironmentId != Guid.Empty && _targets.SelectedItem is not EnvironmentProfile) throw new InvalidOperationException("操作目标已移除，请重新选择；不会自动切换到主桌面。");
            _input.Clear(); _send.IsEnabled = false;
            var sessionId = _session.Id;
            try { await _runtime.SendAsync(sessionId, text, CancellationToken.None, _mentionedSkills.ToArray()); _mentionedSkills.Clear(); UpdateMentionHint(); if (_session?.Id != sessionId) return; FlushText(); var messages = await _runtime.LoadMessagesAsync(sessionId, CancellationToken.None); var reply = messages.LastOrDefault(item => item.Role == "assistant"); if (_session?.Id == sessionId && reply is not null && reply.ToolCalls.Count == 0) ReplyCompleted?.Invoke(reply.Content); }
            catch { if (_session?.Id == sessionId && _input.Text.Length == 0) _input.Text = text; throw; }
            finally { _send.IsEnabled = CanSend(); if (_session?.Id == sessionId) await LoadAssistantProjectAsync(sessionId); }
        });
    }

    private void OnMessageAdded(AssistantMessage message) => Dispatch(() => { if (message.SessionId == _session?.Id) { AddMessageView(message); _scroll.ScrollToEnd(); } });
    private void OnTextChanged(Guid id, string text) => Dispatch(() => _pendingText[id] = text);
    private void OnStatusChanged(string status) => Dispatch(() => { _status.Text = status; _send.IsEnabled = CanSend() && !_runtime.IsBusy; if (_runtime.IsBusy) _assistantChatProject.IsEnabled = false; });
    private bool CanSend()
    {
        if (!_settings.Current.Assistant.Enabled) return false;
        try { _ = AssistantConnections.Chat(_settings.Current.Assistant); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private void FlushText()
    { foreach (var (id, text) in _pendingText) if (_messageViews.TryGetValue(id, out var view) && view.Text != text) { view.Text = text; UpdateSourceLinks(id, text); } _pendingText.Clear(); }
    private void AddMessageView(AssistantMessage message)
    {
        if (_messageViews.ContainsKey(message.Id)) return;
        var panel = new StackPanel(); var header = new TextBlock { Text = RoleName(message.Role), FontSize = 11, Margin = new Thickness(10, 7, 10, 0) }; panel.Children.Add(header); _messageHeaders[message.Id] = (message.Role, header);
        var text = new TextBox { Text = message.Content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(10), FontSize = 14, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        text.SetResourceReference(ForegroundProperty, "SettingsInkBrush"); var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" }; copy.Click += (_, _) => { var content = text.SelectedText.Length > 0 ? text.SelectedText : text.Text; if (content.Length > 0) { try { Clipboard.SetText(content); } catch (System.Runtime.InteropServices.COMException) { _status.Text = "剪贴板暂时不可用。"; } } }; menu.Items.Add(copy);
        var memory = new MenuItem { Header = "保存为记忆" }; memory.Click += (_, _) => _ = SaveMemoryAsync(message, text.SelectedText.Length > 0 ? text.SelectedText : text.Text); menu.Items.Add(memory); text.ContextMenu = menu;
        panel.Children.Add(text); var sources = new WrapPanel { Margin = new Thickness(10, 0, 10, 6) }; panel.Children.Add(sources); _messageSources[message.Id] = sources; UpdateSourceLinks(message.Id, message.Content);
        AddAssistantCitationButtons(panel, message);
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 5, 0, 5) }; border.SetResourceReference(Border.BackgroundProperty, "SettingsCardSubtleBrush"); _messages.Children.Add(border); _messageViews[message.Id] = text;
    }

    private async Task SaveMemoryAsync(AssistantMessage source, string text)
    {
        await UiActionAsync(async () =>
        {
            if (_store is not IAssistantKnowledgeStore library) return;
            var persisted = (await _store.ReadMessagesAsync(source.SessionId, CancellationToken.None)).Any(message => message.Id == source.Id);
            var memory = new AssistantMemory(Guid.NewGuid(), text[..Math.Min(text.Length, 4000)], persisted ? source.Id : null, 1, true, DateTimeOffset.UtcNow) { ProjectId = await _runtime.GetProjectAsync(source.SessionId, CancellationToken.None) };
            var edited = AssistantMemoryEditor.Edit(this, memory, await library.ListProjectsAsync(CancellationToken.None)); if (edited is null) return;
            await _runtime.CheckMemoryAsync(edited.Content + "\n" + edited.Key, CancellationToken.None); await _store.SaveMemoryAsync(edited, CancellationToken.None); _status.Text = "记忆已保存，可在助手设置中管理。";
        });
    }
    private string RoleName(string role) => role switch { "user" => _settings.Current.Assistant.UserDisplayName, "assistant" => _settings.Current.Assistant.DisplayName, "tool" => "工具回执", _ => role };
    private void UpdateSourceLinks(Guid id, string text)
    {
        if (!_messageSources.TryGetValue(id, out var panel)) return;
        panel.Children.Clear();
        foreach (var source in AssistantSearchService.FindSources(text).Take(8))
        {
            var button = ActionButton("↗ " + new Uri(source.Url).Host, (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source.Url) { UseShellExecute = true }); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = "无法打开来源链接。"; }
            });
            button.ToolTip = source.Url; panel.Children.Add(button);
        }
    }
    private async Task UiActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = "已停止。"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Data.Sqlite.SqliteException or System.Net.Http.HttpRequestException)
        { _status.Text = error is InvalidOperationException or InvalidDataException ? error.Message : $"操作未完成（{error.GetType().Name}），请检查连接和设置。"; }
    }

    public void SetRecognizedText(string text, bool autoSend, Guid? expectedSession = null)
    { Dispatch(() => { if (expectedSession is not null && expectedSession != _session?.Id) return; _input.Text = text; if (autoSend && !_runtime.IsBusy) _ = SendMessageAsync(); }); }
    public void SetVoiceState(bool recording, string status) => Dispatch(() => { _record.Content = Glyph(recording ? "\uE71A" : "\uE720"); _record.ToolTip = recording ? "停止录音" : "语音"; _status.Text = status; });
    private void ScheduleSave() { if (_applying || !_positionRestored || !IsVisible || _disposed) return; _save.Stop(); _save.Start(); }
    private void Dispatch(Action action) { if (!_disposed && !Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(() => { if (!_disposed) action(); }); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _runtime.Cancel(); StopSpeechRequested?.Invoke(); _save.Stop(); _render.Stop();
        _runtime.MessageAdded -= OnMessageAdded; _runtime.TextChanged -= OnTextChanged; _runtime.StatusChanged -= OnStatusChanged;
        _runtime.ReferencesChanged -= OnAssistantReferencesChanged;
        _runtime.ConfirmOutbound = null; _runtime.ConfirmTool = null; Close(); GC.SuppressFinalize(this);
    }
}
