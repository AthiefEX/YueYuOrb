using FloatDesk.App.Assistant;
using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantTaskBridgeTests
{
    [Theory]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Tools\\node.exe")]
    [InlineData("C:\\Tools\\unreviewed.ps1")]
    public async Task DirectLaunchCannotBypassReviewedScriptTasks(string path)
    {
        var settings = new Settings();
        await settings.UpdateAsync(current => current with { Assistant = current.Assistant with { AllowComputerControl = true } }, CancellationToken.None);
        var bridge = new AssistantTaskBridge(settings, (_, _, _) => throw new InvalidOperationException("must not execute"));
        var args = System.Text.Json.JsonSerializer.Serialize(new { path, arguments = new[] { path } });
        Assert.Throws<InvalidOperationException>(() => bridge.Preview(new("call", "application_launch", args), GlobalTaskTarget.Id));
    }

    [Fact]
    public async Task SavingTaskIsPreviewedThenEnabledWithoutUsingMcpClientTrust()
    {
        var settings = new Settings(); var calls = 0;
        var bridge = new AssistantTaskBridge(settings, (task, run, token) => { calls++; return Task.FromResult(new DesktopTaskRunResult(run, task.Id, DesktopTaskRunOutcome.Succeeded, 1, null, null)); });
        var call = new AssistantToolCall("save-1", "task_save", "{\"name\":\"test delay\",\"enabled\":true,\"trigger\":{\"kind\":\"Scheduled\",\"localTime\":\"23:59\",\"timeZoneId\":\"China Standard Time\"},\"steps\":[{\"kind\":\"Delay\",\"parameters\":{\"milliseconds\":\"1\"}}]}");
        var preview = bridge.Preview(call, GlobalTaskTarget.Id);
        Assert.True(preview.AlwaysConfirm); Assert.Equal(0, calls); Assert.Empty(settings.Current.DesktopSessions.Tasks);
        var result = await bridge.ExecuteAsync(preview, CancellationToken.None);
        Assert.True(result.Success); var task = Assert.Single(settings.Current.DesktopSessions.Tasks);
        Assert.True(task.Enabled); Assert.Equal(TaskTriggerKind.Scheduled, task.Trigger.Kind); Assert.True(DesktopTaskPackagePolicy.IsApproved(task)); Assert.Equal("assistant", task.Source);
        Assert.Empty(settings.Current.DesktopSessions.AutomationClients);
    }

    [Fact]
    public void DisabledControlAndWrongTargetAreRejectedBeforeRunningAnything()
    {
        var bridge = new AssistantTaskBridge(new Settings(), (_, _, _) => throw new InvalidOperationException("must not execute"));
        Assert.Throws<InvalidOperationException>(() => bridge.Preview(new("call", "desktop_step", "{\"kind\":\"Delay\",\"parameters\":{}}"), GlobalTaskTarget.Id));
        Assert.Throws<InvalidOperationException>(() => bridge.Preview(new("call", "task_save", "{\"name\":\"wrong\",\"environmentId\":\"00000000-0000-0000-0000-000000000001\",\"steps\":[]}"), GlobalTaskTarget.Id));
    }

    private sealed class Settings : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault() with { Assistant = new() { Enabled = true, SupportsTools = true, Endpoint = "https://example.test/v1", Model = "test", AllowTaskManagement = true } };
        public event Action<AppSettings>? Changed;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken token)
        { Current = update(Current); Changed?.Invoke(Current); return Task.FromResult(new OperationResult(true, null, null)); }
    }
}
