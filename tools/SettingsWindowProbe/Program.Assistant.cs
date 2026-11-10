using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using FloatDesk.App;
using FloatDesk.App.Assistant;
using FloatDesk.App.Views;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;
using TabControl = System.Windows.Controls.TabControl;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace SettingsWindowProbe;

internal static partial class Program
{
    private static readonly string[] MergedOrbCategoryNames = ["悬浮球", "轮盘与快捷应用", "悬浮浏览器", "实时翻译", "音频", "亮度"];
    private static readonly string[] MovedOrbCategoryNames = ["悬浮浏览器", "实时翻译", "音频", "亮度"];
    private static async Task VerifyAssistantUiAsync()
    {
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ai-assistant-evidence"); Directory.CreateDirectory(output);
        var paths = new AppDataPaths(Path.Combine(output, "isolated-profile-" + Guid.NewGuid().ToString("N"))); paths.EnsureCreated();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = ServeAssistantProbeAsync(listener, token);
        var settings = AppSettings.CreateDefault() with { Assistant = new() { Enabled = true, Endpoint = $"http://127.0.0.1:{port}/v1", Model = "isolated-probe" } };
        var coordinator = new AssistantProbeCoordinator(settings); using var store = new AssistantStore(Path.Combine(paths.Root, "assistant", "assistant.db"));
        using var http = AssistantApiClient.CreateHttpClient(); using var runtime = new AssistantRuntime(() => coordinator.Current.Assistant, store, new AssistantApiClient(http), new AssistantProbeCredentials());
        using var window = new AssistantWindow(paths, coordinator, store, runtime, () => { });
        coordinator.Changed += value => { runtime.ApplySettings(value.Assistant); window.ApplySettings(value); };
        var settingsWindow = new SettingsWindow(coordinator, paths, null, new NullCredentialStore(), new NullTranslationProviderFactory(), () => [], assistantStore: store, assistantCredentials: new AssistantProbeCredentials());
        try
        {
            settingsWindow.Show(); settingsWindow.SelectAssistantPage(); settingsWindow.UpdateLayout(); await Task.Delay(450, token);
            var tabs = (TabControl)settingsWindow.FindName("SettingsTabControl"); var assistantTab = (TabItem)tabs.SelectedItem;
            if (assistantTab.Header?.ToString() != "AI助手" || assistantTab.Content is not TabControl subTabs || subTabs.Items.Count != 7 || !subTabs.Items.OfType<TabItem>().Any(tab => tab.Header?.ToString() == "知识库")) throw new InvalidOperationException("Assistant settings are missing their seven categories including knowledge.");
            var orbTab = tabs.Items.OfType<TabItem>().Single(tab => System.Windows.Automation.AutomationProperties.GetName(tab) == "悬浮球与轮盘");
            if (orbTab.Content is not TabControl orbCategories || !orbCategories.Items.OfType<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(MergedOrbCategoryNames)) throw new InvalidOperationException("Orb navigation did not merge the requested settings.");
            if (tabs.Items.OfType<TabItem>().Any(tab => MovedOrbCategoryNames.Contains(System.Windows.Automation.AutomationProperties.GetName(tab)))) throw new InvalidOperationException("Duplicate top-level settings remain.");
            tabs.SelectedItem = orbTab;
            foreach (var category in orbCategories.Items.OfType<TabItem>()) { orbCategories.SelectedItem = category; settingsWindow.UpdateLayout(); }
            CaptureProbeWindow(settingsWindow, Path.Combine(output, "orb-merged-settings.png")); tabs.SelectedItem = assistantTab;
            Console.WriteLine("settings.realMergedOrbWheelTranslationAudioBrightness=PASS");
            CaptureProbeWindow(settingsWindow, Path.Combine(output, "assistant-settings.png")); settingsWindow.Hide();
            window.Show(); await Task.Delay(250, token); window.UpdateLayout();
            window.MessageInput.Text = "本地助手窗口测试";
            var sending = (Task)typeof(AssistantWindow).GetMethod("SendMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            await sending.WaitAsync(TimeSpan.FromSeconds(20), token); await responseTask; await Task.Delay(150, token);
            var session = (await store.ListSessionsAsync(token))[0]; var transcript = await store.ReadMessagesAsync(session.Id, token);
            if (transcript.Count != 2 || transcript[0].Content != "本地助手窗口测试" || transcript[1].Content != "本地流式测试回复") throw new InvalidOperationException("Real loopback/SSE chat did not persist correctly.");
            if (!Descendants(window).OfType<System.Windows.Controls.TextBox>().Any(text => text.IsReadOnly && text.Text == "本地流式测试回复")) throw new InvalidOperationException("The real assistant window failed to display streamed text.");
            await coordinator.UpdateAsync(current => current with { Assistant = current.Assistant with { WindowTitle = "自定义助手窗口", DisplayName = "小月", UserDisplayName = "测试用户", Window = current.Assistant.Window with { AlwaysOnTop = false } } }, token);
            if (window.Title != "自定义助手窗口" || window.Topmost || !Descendants(window).OfType<TextBlock>().Any(text => text.Text == "小月") || !Descendants(window).OfType<TextBlock>().Any(text => text.Text == "测试用户")) throw new InvalidOperationException("Assistant names/topmost did not update on the real window.");
            await VerifyAssistantConnectionsUiAsync(settingsWindow, window, coordinator, output, token);
            await VerifyAssistantKnowledgeUiAsync(settingsWindow, window, coordinator, store, runtime, paths, output, token);
            await VerifyAssistantWorkspaceUiAsync(settingsWindow, window, coordinator, store, output, token);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var width in new[] { 480d, 720d })
            { ThemeService.Apply(theme); await coordinator.UpdateAsync(current => current with { General = current.General with { Theme = theme } }, token); window.Width = width; window.UpdateLayout(); CaptureProbeWindow(window, Path.Combine(output, $"assistant-{theme}-{width:0}.png")); }
            var scroll = (FrameworkElement)typeof(AssistantWindow).GetField("_scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            await VerifyWindowBackdropTransparencyAsync(window, window.WindowOpacitySlider, scroll, "assistant");
            window.Close(); if (window.IsVisible) throw new InvalidOperationException("Assistant close should hide a reusable window.");
            window.Show(); window.UpdateLayout();
            var panel = new QuickPanelWindow(); panel.Apply(coordinator.Current, false, "probe");
            if (((FrameworkElement)panel.FindName("AssistantButton")).Visibility != Visibility.Visible) throw new InvalidOperationException("Enabled assistant has no wheel entry.");
            panel.Apply(coordinator.Current with { Assistant = coordinator.Current.Assistant with { Enabled = false } }, false, "probe");
            if (((FrameworkElement)panel.FindName("AssistantButton")).Visibility != Visibility.Collapsed) throw new InvalidOperationException("Disabled assistant retains an active wheel entry."); panel.Close();
            Console.WriteLine("assistant.realSettingsSevenTabs=PASS;realLoopbackSseChatAndSqlite=PASS;realWindowResizeHideReopen=PASS;wheelEnableDisable=PASS");
        }
        finally { settingsWindow.Hide(); window.Dispose(); listener.Stop(); }
    }

    private static async Task ServeAssistantProbeAsync(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token); using var stream = client.GetStream(); using var headers = new MemoryStream(); var one = new byte[1];
        while (headers.Length < 32768)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Probe request ended early."); headers.WriteByte(one[0]);
            if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        var header = Encoding.ASCII.GetString(headers.ToArray());
        var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (length > 1024 * 1024) throw new InvalidDataException("Probe request too large."); var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        using var json = JsonDocument.Parse(body); if (!json.RootElement.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("content").GetString() == "本地助手窗口测试")) throw new InvalidDataException("Missing user input in actual request.");
        var response = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"本地流式\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"测试回复\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, token); await stream.WriteAsync(response.AsMemory(0, 23), token); await Task.Delay(30, token); await stream.WriteAsync(response.AsMemory(23), token); await stream.FlushAsync(token);
    }

    private sealed class AssistantProbeCoordinator(AppSettings initial) : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = initial;
        public event Action<AppSettings>? Changed;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
        { Current = update(Current); Changed?.Invoke(Current); return Task.FromResult(OperationResult.Ok()); }
    }
    private sealed class AssistantProbeCredentials : IAssistantCredentialStore
    {
        private readonly Dictionary<(string, string), string> _values = [];
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult(_values.GetValueOrDefault((purpose, endpoint)));
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) { _values[(purpose, endpoint)] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) { _values.Remove((purpose, endpoint)); return Task.CompletedTask; }
    }

    private static async Task VerifyAssistantConnectionsUiAsync(SettingsWindow settingsWindow, AssistantWindow assistantWindow, AssistantProbeCoordinator coordinator, string output, CancellationToken token)
    {
        var original = coordinator.Current; using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeModelDiscoveryProbeAsync(listener, token);
        T Field<T>(string name) => (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settingsWindow)!;
        object Invoke(string name, params object[] args) => typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settingsWindow, args)!;
        try
        {
            settingsWindow.Show(); settingsWindow.SelectAssistantPage(); settingsWindow.UpdateLayout();
            var add = Descendants(settingsWindow).OfType<Button>().Single(button => button.Content?.ToString() == "新增密钥");
            foreach (var label in new[] { "A", "B" })
            {
                Exception? modalFailure = null;
                _ = settingsWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var modal = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "新增密钥");
                        Field<TextBox>("_assistantProfileName").Text = label;
                        Field<TextBox>("_assistantProfileEndpoint").Text = $"http://127.0.0.1:{port}/v1";
                        Field<PasswordBox>("_assistantKey").Password = "synthetic-" + label;
                        if (label == "A") CaptureProbeWindow(modal, Path.Combine(output, "assistant-key-editor.png"));
                        Descendants(modal).OfType<Button>().Single(button => button.Content?.ToString() == "保存并读取模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { modalFailure = error; Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "新增密钥")?.Close(); }
                }));
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (modalFailure is not null) throw new InvalidOperationException("Key editor failed.", modalFailure);
                for (var wait = 0; wait < 100 && !Field<ComboBox>("_assistantModels").Items.Cast<string>().Contains("probe-model"); wait++) await Task.Delay(25, token);
                var models = Field<ComboBox>("_assistantModels");
                if (!models.Items.Cast<string>().Contains("probe-model")) throw new InvalidOperationException("Real model discovery did not populate the picker.");
                models.ApplyTemplate();
                var manualModel = typeof(SettingsWindow).GetField("_assistantManualModel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(settingsWindow) as TextBox ?? models.Template.FindName("PART_EditableTextBox", models) as TextBox;
                if (manualModel is null) throw new InvalidOperationException("Model picker has no editable input for providers without model discovery.");
                manualModel.Text = "manual-probe";
                if (models.Text != "manual-probe") throw new InvalidOperationException("Manual model did not reach the saved selection.");
                manualModel.Text = "probe-model";
                models.Text = "probe-model";
                await (Task)Invoke("UseAssistantProfileAsync", label == "B");
            }
            await (Task)Invoke("TestAssistantProfileModelAsync");
            if (!Field<TextBox>("_assistantModelResult").Text.Contains("OK", StringComparison.Ordinal)) throw new InvalidOperationException("Actual probe reply is missing.");
            var beforeBatch = coordinator.Current.Assistant;
            _ = settingsWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                var confirmation = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "全部检测模型");
                Descendants(confirmation).OfType<Button>().Single(button => button.Content?.ToString() == "同意本次操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            await (Task)Invoke("TestAssistantModelsAsync", true); await serving;
            var batchResult = Field<TextBox>("_assistantModelResult").Text;
            if (!batchResult.Contains("probe-model：通过", StringComparison.Ordinal) || !batchResult.Contains("probe-alt：未通过", StringComparison.Ordinal) || coordinator.Current.Assistant != beforeBatch) throw new InvalidOperationException("Batch tests lost per-model results or changed configured model.");
            var modelSwitch = Field<CheckBox>("_assistantModelEnabled"); modelSwitch.IsChecked = false; modelSwitch.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Task.Delay(80, token);
            try { _ = AssistantConnections.Search(coordinator.Current.Assistant); throw new InvalidOperationException("Disabled model remained usable."); } catch (InvalidOperationException error) when (error.Message.Contains("模型已关闭", StringComparison.Ordinal)) { }
            modelSwitch.IsChecked = true; modelSwitch.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Task.Delay(80, token);
            if (coordinator.Current.Assistant.Profiles.Count != 3 || coordinator.Current.Assistant.ActiveProfileId == coordinator.Current.Assistant.SearchProfileId) throw new InvalidOperationException("Chat and search profile selection did not stay independent.");
            var rows = Field<System.Windows.Controls.ListBox>("_assistantProfileRows"); var toggle = Descendants(rows).OfType<CheckBox>().Single(item => item.ToolTip?.ToString() == "A · 模型开关");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Task.Delay(80, token);
            if (Descendants(assistantWindow).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "发送").IsEnabled) throw new InvalidOperationException("Disabled key left the send button enabled.");
            try { _ = AssistantConnections.Chat(coordinator.Current.Assistant); throw new InvalidOperationException("Disabled profile silently fell back."); }
            catch (InvalidOperationException error) when (error.Message.Contains("停用", StringComparison.Ordinal) || error.Message.Contains("模型已关闭", StringComparison.Ordinal)) { }
            rows.BringIntoView(); settingsWindow.UpdateLayout(); CaptureProbeWindow(settingsWindow, Path.Combine(output, "assistant-settings-connections.png"));
            Field<TextBox>("_assistantModelResult").BringIntoView(); settingsWindow.UpdateLayout(); CaptureProbeWindow(settingsWindow, Path.Combine(output, "assistant-model-result.png"));
            Console.WriteLine("assistant.realMultiKeyUi=PASS;realKeyEditorModal=PASS;realSingleAndBatchModels=PASS;realModelDiscoveryAndProbe=PASS;separateChatSearchProfiles=PASS;disabledProfileNoFallback=PASS;customNamesAndTopmost=PASS");
        }
        finally { await coordinator.UpdateAsync(_ => original, CancellationToken.None); settingsWindow.Hide(); listener.Stop(); }
    }

    private static async Task ServeModelDiscoveryProbeAsync(TcpListener listener, CancellationToken token)
    {
        for (var index = 0; index < 5; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(token); using var stream = client.GetStream(); using var headerBytes = new MemoryStream(); var one = new byte[1];
            while (headerBytes.Length < 32768)
            {
                await stream.ReadExactlyAsync(one, token); headerBytes.WriteByte(one[0]);
                if (headerBytes.Length >= 4 && headerBytes.GetBuffer().AsSpan((int)headerBytes.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
            if (!headers.Contains("Authorization: Bearer synthetic-" + (index == 0 ? "A" : "B"), StringComparison.Ordinal)) throw new InvalidOperationException("Wrong profile key reached loopback probe.");
            var requestedModel = "";
            if (index >= 2)
            {
                var length = int.Parse(headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
                using var json = JsonDocument.Parse(body); requestedModel = json.RootElement.GetProperty("model").GetString() ?? "";
                if (index == 2 ? requestedModel != "probe-model" : requestedModel is not ("probe-model" or "probe-alt")) throw new InvalidOperationException("Model probe used wrong model.");
            }
            var bytes = Encoding.UTF8.GetBytes(index < 2 ? "{\"data\":[{\"id\":\"probe-model\"},{\"id\":\"probe-alt\"}]}" : "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(requestedModel == "probe-alt" ? "503 Unavailable" : "200 OK")}\r\nContent-Type: {(index < 2 ? "application/json" : "text/event-stream")}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
            await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
    }
}
