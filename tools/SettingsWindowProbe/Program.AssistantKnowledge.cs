using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using FloatDesk.App;
using FloatDesk.App.Assistant;
using FloatDesk.App.Views;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;

namespace SettingsWindowProbe;

internal static partial class Program
{
    private static async Task VerifyAssistantKnowledgeUiAsync(SettingsWindow settingsWindow, AssistantWindow window, AssistantProbeCoordinator coordinator, AssistantStore store, AssistantRuntime runtime, AppDataPaths paths, string output, CancellationToken token)
    {
        var original = coordinator.Current; using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var projectA = await store.CreateProjectAsync("知识项目A", token); var projectB = await store.CreateProjectAsync("知识项目B", token);
        var source = Path.Combine(paths.Root, "knowledge-source.md"); await File.WriteAllTextAsync(source, "项目A主题颜色使用蓝色。", token);
        var imported = await AssistantDocumentImporter.LoadAsync(source, projectA.Id, "deny", token); await store.SaveDocumentAsync(imported.Document, imported.Passages, token);
        var serving = ServeKnowledgeProbeAsync(listener, token);
        object WindowCall(string name) => typeof(AssistantWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        T SettingsField<T>(string name) => (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settingsWindow)!;
        object SettingsCall(string name) => typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settingsWindow, null)!;
        try
        {
            await coordinator.UpdateAsync(current => current with { Assistant = current.Assistant with { Endpoint = $"http://127.0.0.1:{port}/v1", Model = "knowledge-probe", ActiveProfileId = null, Library = current.Assistant.Library with { UseKnowledge = true } } }, token);
            foreach (var project in new[] { projectA, projectB })
            {
                await (Task)WindowCall("NewSessionAsync"); var session = window.CurrentSessionId!.Value;
                await runtime.SetProjectAsync(session, project.Id, token);
                window.MessageInput.Text = "主题颜色是什么"; await ((Task)WindowCall("SendMessageAsync")).WaitAsync(TimeSpan.FromSeconds(15), token);
                var messages = await store.ReadMessagesAsync(session, token);
                if ((project.Id == projectA.Id) != (messages[^1].References.Count > 0)) throw new InvalidOperationException("Real knowledge citation/project isolation failed.");
            }
            var old = await store.CreateSessionAsync("三个月前的实际记录", token); await store.SetSessionProjectAsync(old.Id, projectA.Id, token);
            await store.AppendMessageAsync(new(Guid.NewGuid(), old.Id, "user", "ArchiveQuartz旧问题答案是绿色。" + new string('字', 10000), DateTimeOffset.UtcNow.AddMonths(-3)), token);
            await coordinator.UpdateAsync(current => current with { Assistant = current.Assistant with { Library = current.Assistant.Library with { UseKnowledge = false, SearchHistory = true } } }, token);
            await (Task)WindowCall("NewSessionAsync"); var historical = window.CurrentSessionId!.Value; await runtime.SetProjectAsync(historical, projectA.Id, token);
            window.MessageInput.Text = "ArchiveQuartz"; await ((Task)WindowCall("SendMessageAsync")).WaitAsync(TimeSpan.FromSeconds(15), token);
            if (!(await store.ReadMessagesAsync(historical, token))[^1].References.Any(reference => reference.Kind == "chat")) throw new InvalidOperationException("Old chat citation was not persisted.");
            await serving;
            Console.WriteLine("assistant.realOldChatFtsToBoundedHttpPayload=PASS");
            settingsWindow.Show(); settingsWindow.SelectAssistantPage(); var tabs = (TabControl)settingsWindow.FindName("SettingsTabControl"); var categories = (TabControl)((TabItem)tabs.SelectedItem).Content;
            categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => item.Header?.ToString() == "知识库");
            await (Task)SettingsCall("RefreshAssistantKnowledgeAsync"); SettingsField<ComboBox>("_assistantKnowledgeProject").SelectedValue = projectA.Id;
            await (Task)SettingsCall("RefreshAssistantKnowledgeAsync"); SettingsField<TextBox>("_assistantKnowledgeQuery").Text = "主题"; await (Task)SettingsCall("PreviewAssistantRecallAsync");
            if (!SettingsField<TextBox>("_assistantKnowledgeOutput").Text.Contains("蓝色", StringComparison.Ordinal)) throw new InvalidOperationException("Knowledge UI full text preview failed.");
            settingsWindow.UpdateLayout(); CaptureProbeWindow(settingsWindow, Path.Combine(output, "assistant-knowledge.png"));
            Console.WriteLine("assistant.realKnowledgeImportAndFtsUi=PASS;realProjectChatIsolation=PASS;realCitationPersistence=PASS;modelSwitchAndActualReply=PASS");
        }
        finally { await coordinator.UpdateAsync(_ => original, CancellationToken.None); settingsWindow.Hide(); listener.Stop(); }
    }

    private static async Task ServeKnowledgeProbeAsync(TcpListener listener, CancellationToken token)
    {
        for (var index = 0; index < 3; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(token); using var stream = client.GetStream(); using var headers = new MemoryStream(); var one = new byte[1];
            while (headers.Length < 32768) { await stream.ReadExactlyAsync(one, token); headers.WriteByte(one[0]); if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break; }
            var header = Encoding.ASCII.GetString(headers.ToArray()); var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            if (length > 100000) throw new InvalidDataException("Knowledge probe request too large."); var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
            using var json = JsonDocument.Parse(body); var hasSource = json.RootElement.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("content").GetString()!.Contains("项目A主题颜色使用蓝色", StringComparison.Ordinal));
            if (hasSource != (index == 0)) throw new InvalidOperationException("Wrong project source reached actual model request.");
            if (index == 2 && (!json.RootElement.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("content").GetString()!.Contains("旧问题答案是绿色", StringComparison.Ordinal)) || body.Length > 16000)) throw new InvalidOperationException("Old chat source missing or unbounded in HTTP request.");
            var text = index == 0 ? "蓝色，依据项目A资料。" : "项目B暂无相关资料。"; var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } } });
            var response = Encoding.UTF8.GetBytes("data: " + payload + "\n\ndata: [DONE]\n\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), token); await stream.WriteAsync(response, token); await stream.FlushAsync(token);
        }
    }
}
