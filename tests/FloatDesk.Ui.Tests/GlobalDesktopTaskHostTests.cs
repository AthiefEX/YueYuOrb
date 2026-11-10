using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class GlobalDesktopTaskHostTests
{
    [Fact]
    public async Task AssistantApprovedTaskSurvivesReloadAndExecutesWithoutAssistantOrApi()
    {
        var root = Path.Combine(Path.GetTempPath(), "yueyu-assistant-task-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "result.txt");
        try
        {
            var coordinator = new AssistantCoordinator();
            var bridge = new FloatDesk.App.Assistant.AssistantTaskBridge(coordinator, (_, _, _) => throw new InvalidOperationException("Saving cannot execute."));
            var parameters = new Dictionary<string, string> { ["scriptName"] = "marker.ps1", ["arguments"] = System.Text.Json.JsonSerializer.Serialize(new[] { marker }) };
            var args = System.Text.Json.JsonSerializer.Serialize(new { name = "harmless persisted marker", enabled = true,
                trigger = new { kind = "Scheduled", localTime = "23:59", timeZoneId = "China Standard Time" },
                steps = new[] { new { kind = "RunScript", parameters } },
                scripts = new[] { new { name = "marker.ps1", content = "param([string]$path)\n[IO.File]::WriteAllText($path,'assistant-task-real')" } } });
            var preview = bridge.Preview(new("save", "task_save", args), GlobalTaskTarget.Id); Assert.True(preview.AlwaysConfirm);
            Assert.True((await bridge.ExecuteAsync(preview, CancellationToken.None)).Success); Assert.False(File.Exists(marker));
            using var store = new FloatDesk.Infrastructure.Settings.JsonSettingsStore(root);
            await store.SaveAsync(coordinator.Current with { Assistant = coordinator.Current.Assistant with { Enabled = false } }, CancellationToken.None);
            var reloaded = await store.LoadAsync(CancellationToken.None); Assert.False(reloaded.Assistant.Enabled);
            var task = Assert.Single(reloaded.DesktopSessions.Tasks); Assert.True(DesktopTaskPackagePolicy.IsApproved(task));
            using var host = new GlobalDesktopTaskHost(new NoChildHost(), new Brightness());
            var result = await host.RunTaskAsync(task, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(DesktopTaskRunOutcome.Succeeded, result.Outcome); Assert.Equal("assistant-task-real", await File.ReadAllTextAsync(marker));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class AssistantCoordinator : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault() with { Assistant = new() { Enabled = true, SupportsTools = true, Endpoint = "https://example.test/v1", Model = "test", AllowTaskManagement = true } };
        public event Action<AppSettings>? Changed;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken token)
        { Current = update(Current); Changed?.Invoke(Current); return Task.FromResult(OperationResult.Ok()); }
    }

    [Fact]
    public async Task ExplicitGlobalPipelineExecutesVerifiedScriptAndUsesBrightnessService()
    {
        var marker=Path.Combine(Path.GetTempPath(),"yueyu-global-"+Guid.NewGuid().ToString("N")+".txt");
        var brightness=new Brightness();var inner=new NoChildHost();
        using var host=new GlobalDesktopTaskHost(inner,brightness);
        var asset=DesktopTaskScript.Create("demo.ps1","param([string]$path)\n[IO.File]::WriteAllText($path,'全局脚本成功')");
        var task=new DesktopSessionTask(1,Guid.NewGuid(),"global probe",GlobalTaskTarget.Id,
            [new(Guid.NewGuid(),SessionCommandKind.RunScript,new Dictionary<string,string>{{"scriptName",asset.Name},{"arguments",System.Text.Json.JsonSerializer.Serialize(new[]{marker})}},20,0),
             new(Guid.NewGuid(),SessionCommandKind.SetBrightness,new Dictionary<string,string>{{"percent","37"}},5,0)],new(TaskTriggerKind.Manual,null,null),1,true){Scripts=[asset]};
        try
        {
            var result=await host.RunTaskAsync(task,Guid.NewGuid(),CancellationToken.None);
            Assert.Equal(DesktopTaskRunOutcome.Succeeded,result.Outcome);
            Assert.Equal("全局脚本成功",await File.ReadAllTextAsync(marker));
            Assert.Equal(37,brightness.LastPercent);Assert.Equal(0,inner.Calls);
        }
        finally{if(File.Exists(marker))File.Delete(marker);}
    }
    private sealed class Brightness:IBrightnessService
    {
        public int LastPercent;
        public void Dispose(){}
        public Task<BrightnessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)=>Task.FromResult(new BrightnessSnapshot([new("test","Synthetic monitor",50,null,true)]));
        public Task<BrightnessWriteResult> SetPercentAsync(string deviceId,int percent,CancellationToken cancellationToken){LastPercent=percent;return Task.FromResult(new BrightnessWriteResult(deviceId,percent,null));}
    }
    private sealed class NoChildHost:IDesktopSessionExecutionHost
    {
        public int Calls;
        public bool IsEnvironmentReady(Guid environmentId)=>false;
        public Guid? GetLoginInstanceId(Guid environmentId)=>null;
        public SessionRuntimeStatus GetStatus(Guid environmentId)=>SessionRuntimeStatus.Stopped;
        public Task<DesktopSessionActionStatus> StartEnvironmentAsync(EnvironmentProfile environment,CancellationToken cancellationToken){Calls++;throw new InvalidOperationException();}
        public Task<DesktopSessionActionStatus> StopEnvironmentAsync(Guid environmentId,CancellationToken cancellationToken){Calls++;throw new InvalidOperationException();}
        public Task<DesktopTaskRunResult> RunTaskAsync(DesktopSessionTask task,Guid runId,CancellationToken cancellationToken){Calls++;throw new InvalidOperationException();}
        public Task<bool> CancelRunAsync(Guid runId,CancellationToken cancellationToken)=>Task.FromResult(false);
    }
}
