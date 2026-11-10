using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopAutomationControlTests
{
    [Fact]
    public async Task TrustedConnectionPreservesEnabledTriggerAndCannotBeStolenOrUsedAfterRevocation()
    {
        var settings=new Settings();var host=new Host();
        var registry=new AutomationConnectionRegistry(settings,(_,name)=>new("verified-client",name,"C:\\Tools\\client.exe",new string('a',64),false,DateTimeOffset.UtcNow));
        var prompts=0;registry.ConfirmationRequested+=_=>prompts++;
        using var service=new DesktopSessionApplicationService(()=>settings.Current,new Ledger(),host);
        using var control=new DesktopAutomationControlHandler(settings,service,registry);
        var caller=new AutomationCaller(21,1,DateTimeOffset.UtcNow);
        var connection=await control.HandleAsync(new(1,"automation.connect",null,null,null){ClientName="Agent",VerifiedCaller=caller},CancellationToken.None);
        Assert.Equal("Pending",connection.ConnectionState);Assert.Equal(1,prompts);
        var task=Sample(settings) with {Trigger=new(TaskTriggerKind.EnvironmentReady,null,null)};
        var save=new DesktopSessionControlRequest(1,"automation.save",task.EnvironmentId,task.Id,null)
            {TaskDefinition=task,ClientConnectionId=connection.ClientConnectionId,VerifiedCaller=caller};
        Assert.Equal("ConnectionNotTrusted",(await control.HandleAsync(save,CancellationToken.None)).ErrorCode);
        await registry.SetEnabledAsync("verified-client",true,CancellationToken.None);
        var saved=await control.HandleAsync(save,CancellationToken.None);
        Assert.True(saved.Success);Assert.True(saved.TaskDefinition!.Enabled);
        Assert.Equal(TaskTriggerKind.EnvironmentReady,saved.TaskDefinition.Trigger.Kind);
        Assert.True(DesktopTaskPackagePolicy.IsApproved(saved.TaskDefinition));
        Assert.Equal("ConnectionNotTrusted",(await control.HandleAsync(save with {VerifiedCaller=caller with {ProcessId=22}},CancellationToken.None)).ErrorCode);
        await registry.SetEnabledAsync("verified-client",false,CancellationToken.None);
        Assert.Equal("ConnectionNotTrusted",(await control.HandleAsync(save,CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task EnablingGlobalManualTaskExecutesWithoutEnablingDesktopSessions()
    {
        var source=new Settings();var store=new MemoryStore(source.Current with {DesktopSessions=new(false,source.Current.DesktopSessions.Environments,[])});
        using var settings=new FloatDesk.Infrastructure.Settings.SettingsCoordinator(store);
        var host=new Host();using var service=new DesktopSessionApplicationService(()=>settings.Current,new Ledger(),host);
        using var activation=new AutomationTaskActivation(settings,service);
        await settings.InitializeAsync(CancellationToken.None);
        var global=Sample(source) with {EnvironmentId=GlobalTaskTarget.Id,McpEnvironmentBinding=null,Enabled=true};
        Assert.True((await settings.UpdateAsync(current=>current with {DesktopSessions=current.DesktopSessions with {Tasks=[global]}},CancellationToken.None)).Success);
        for(var index=0;index<100&&host.Runs==0;index++)await Task.Delay(10);
        Assert.Equal(1,host.Runs);Assert.False(settings.Current.DesktopSessions.Enabled);
        var local=Sample(source);
        var denied=await service.RunAsync(local,Guid.NewGuid(),CancellationToken.None);
        Assert.Equal("BackendModeDisabled",denied.ErrorCode);
        Assert.Equal(1,host.Runs);
    }
    [Fact]
    public async Task ProductionSettingsCoordinatorRetainsConflictCodeAndPersistsDraft()
    {
        var initial=new Settings();var task=Sample(initial);
        var store=new MemoryStore(initial.Current with {DesktopSessions=initial.Current.DesktopSessions with {Tasks=[task]}});
        using var settings=new FloatDesk.Infrastructure.Settings.SettingsCoordinator(store);
        await settings.InitializeAsync(CancellationToken.None);
        using var service=new DesktopSessionApplicationService(()=>settings.Current,new Ledger(),new Host());
        using var control=new DesktopAutomationControlHandler(settings,service);
        var request=new DesktopSessionControlRequest(1,"automation.save",task.EnvironmentId,task.Id,null)
            {TaskDefinition=task with {Rules="production coordinator rules"},ExpectedRevision=99};
        var conflict=await control.HandleAsync(request,CancellationToken.None);
        Assert.False(conflict.Success);Assert.Equal("TaskRevisionConflict",conflict.ErrorCode);
        var saved=await control.HandleAsync(request with {ExpectedRevision=1},CancellationToken.None);
        Assert.True(saved.Success,saved.ErrorCode);
        Assert.Equal("production coordinator rules",Assert.Single(store.Value.DesktopSessions.Tasks).Rules);
        Assert.False(store.Value.DesktopSessions.Tasks[0].Enabled);
    }
    private sealed class MemoryStore(AppSettings initial) : ISettingsStore
    {
        public AppSettings Value=initial;
        public Task<AppSettings> LoadAsync(CancellationToken token)=>Task.FromResult(Value);
        public Task SaveAsync(AppSettings value,CancellationToken token){Value=value;return Task.CompletedTask;}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMcpStdioUsesProtectedPipeAndUnifiedTaskLibrary(bool trustedConnection)
    {
        var settings = new Settings(); var host = new Host();
        using var service = new DesktopSessionApplicationService(() => settings.Current, new Ledger(), host);
        uint observedCaller=0;
        var registry=trustedConnection?new AutomationConnectionRegistry(settings,(caller,name)=>
        {observedCaller=caller.ProcessId;return new("stdio-client",name,"C:\\Synthetic\\client.exe",new string('a',64),false,DateTimeOffset.UtcNow);}):null;
        if(registry is not null)registry.ConfirmationRequested+=client=>registry.SetEnabledAsync(client.Id,true,CancellationToken.None).GetAwaiter().GetResult();
        using var control = new DesktopAutomationControlHandler(settings, service,registry);
        var pipeName = "yueyu-mcp-test-" + Guid.NewGuid().ToString("N");
        await using var server = new FloatDesk.Windows.DesktopSessions.DesktopSessionControlServer(control, pipeName);
        server.Start();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,"FloatDesk.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var paths = new[]
        {
            Path.Combine(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.FullName,"FloatDesk.McpServer","release","YueYuOrb.McpServer.exe"),
            Path.Combine(root.FullName,"artifacts","build","bin","FloatDesk.McpServer","release","YueYuOrb.McpServer.exe"),
            Path.Combine(root.FullName,"src","FloatDesk.McpServer","bin","Release","net10.0-windows10.0.19041.0","YueYuOrb.McpServer.exe")
        };
        var executable = paths.FirstOrDefault(File.Exists);
        Assert.NotNull(executable);
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var nextId = 0;
        async Task<System.Text.Json.JsonElement> Rpc(string method, object parameters)
        {
            var id = ++nextId;
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { jsonrpc="2.0",id,method,@params=parameters }, DesktopTaskPackagePolicy.JsonOptions).ReplaceLineEndings(""));
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            using var response = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal(id,response.RootElement.GetProperty("id").GetInt32());
            return response.RootElement.GetProperty("result").Clone();
        }
        async Task<System.Text.Json.JsonElement> Tool(string name, object arguments)
        {
            var response = await Rpc("tools/call",new { name,arguments });
            var json = response.GetProperty("content")[0].GetProperty("text").GetString()!;
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            return parsed.RootElement.Clone();
        }
        try
        {
            await Rpc("initialize",new { protocolVersion="2025-11-25",capabilities=new {},clientInfo=new {name="probe",version="1"} });
            if(trustedConnection)Assert.Equal((uint)process.Id,observedCaller);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var tools = await Rpc("tools/list",new {}); // Any notification response would make the ID assertion fail.
            Assert.True(tools.GetProperty("tools").GetArrayLength() >= 12);
            var listing = await Tool("environments_list",new {});
            Assert.Equal(settings.Current.DesktopSessions.Environments[0].Id,listing.GetProperty("environments")[0].GetProperty("id").GetGuid());
            var task = Sample(settings) with { Rules="persist through real IPC", Scripts=[DesktopTaskScript.Create("probe.ps1","Get-Date | Out-Null")] };
            var saved = await Tool("tasks_create_draft",new {task});
            Assert.True(saved.GetProperty("success").GetBoolean());
            var draft = Assert.Single(settings.Current.DesktopSessions.Tasks);
            Assert.Equal(trustedConnection,draft.Enabled); Assert.Equal(task.Rules,draft.Rules); Assert.Equal(0,host.Runs);
            var runId = Guid.NewGuid();
            if(!trustedConnection)
            {
                var denied = await Tool("tasks_run",new {taskId=draft.Id,environmentId=draft.EnvironmentId,runId});
                Assert.Equal("NeedsAuthorization",denied.GetProperty("errorCode").GetString());
            }
            var approved = draft with { Enabled=true,McpEnvironmentBinding=DesktopTaskPackagePolicy.EnvironmentBinding(settings.Current.DesktopSessions.Environments[0]) };
            approved = approved with { McpApprovalHash=DesktopTaskPackagePolicy.DefinitionHash(approved) };
            settings.Current = settings.Current with { DesktopSessions=settings.Current.DesktopSessions with {Tasks=[approved]} };
            await Tool("tasks_run",new {taskId=draft.Id,environmentId=draft.EnvironmentId,runId});
            await Tool("tasks_run",new {taskId=draft.Id,environmentId=draft.EnvironmentId,runId});
            for(var index=0;index<100;index++)
            {
                var status=await Tool("tasks_status",new {runId});
                if(status.GetProperty("runOutcome").GetString()=="Succeeded") break;
                await Task.Delay(10,timeout.Token);
            }
            Assert.Equal(1,host.Runs);
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if(!process.HasExited)process.Kill(entireProcessTree:true); }
        }
    }
    [Fact]
    public async Task McpDraftPersistsRulesAndAssetsWithoutExecutionOrApproval()
    {
        var settings = new Settings(); var host = new Host();
        using var service = new DesktopSessionApplicationService(() => settings.Current, new Ledger(), host);
        using var control = new DesktopAutomationControlHandler(settings, service);
        var task = Sample(settings) with { Rules = "agent rules", Scripts = [DesktopTaskScript.Create("check.ps1", "Get-Date")], McpApprovalHash = "forged" };
        var result = await control.HandleAsync(new(1,"automation.save",task.EnvironmentId,task.Id,null) { TaskDefinition = task }, CancellationToken.None);
        Assert.True(result.Success); var saved = Assert.Single(settings.Current.DesktopSessions.Tasks);
        Assert.Equal(task.Rules, saved.Rules); Assert.Single(saved.Scripts);
        Assert.False(saved.Enabled); Assert.Null(saved.McpApprovalHash); Assert.Equal(0,host.Runs);
    }
    [Fact]
    public async Task SubmissionRequiresCurrentLocalApproval()
    {
        var settings = new Settings(); var host = new Host(); var task = Sample(settings);
        settings.Current = settings.Current with { DesktopSessions = settings.Current.DesktopSessions with { Tasks = [task] } };
        using var service = new DesktopSessionApplicationService(() => settings.Current,new Ledger(),host);
        using var control = new DesktopAutomationControlHandler(settings,service);
        var result = await control.HandleAsync(new(1,"automation.run",task.EnvironmentId,task.Id,Guid.NewGuid()),CancellationToken.None);
        Assert.False(result.Success); Assert.Equal("NeedsAuthorization",result.ErrorCode); Assert.Equal(0,host.Runs);
    }
    [Theory]
    [InlineData(DesktopSessionBackendKind.LocalChild)]
    [InlineData(DesktopSessionBackendKind.HyperV)]
    public async Task BothBackendsRequireEnvironmentGrantAndRejectUnreviewedScripts(DesktopSessionBackendKind backend)
    {
        var settings = new Settings(); var host = new Host();
        var environment = settings.Current.DesktopSessions.Environments[0] with {Backend=backend,BackendProfileId=backend==DesktopSessionBackendKind.HyperV?Guid.NewGuid().ToString():"local-child"};
        settings.Current = settings.Current with { DesktopSessions=settings.Current.DesktopSessions with {Environments=[environment]} };
        using var service = new DesktopSessionApplicationService(() => settings.Current,new Ledger(),host);
        using var control = new DesktopAutomationControlHandler(settings,service);
        var request = new DesktopSessionControlRequest(1,"automation.step",environment.Id,null,Guid.NewGuid())
            {Step=new(Guid.NewGuid(),SessionCommandKind.Delay,new Dictionary<string,string>{{"milliseconds","1"}},5,0)};
        Assert.Equal("NeedsAuthorization",(await control.HandleAsync(request,CancellationToken.None)).ErrorCode);
        settings.Current = settings.Current with {DesktopSessions=settings.Current.DesktopSessions with {McpAllowedEnvironments=[environment.Id]}};
        var script=request with {Step=request.Step! with {Kind=SessionCommandKind.RunScript}};
        Assert.Equal("TaskApprovalRequired",(await control.HandleAsync(script,CancellationToken.None)).ErrorCode);
        Assert.True((await control.HandleAsync(request,CancellationToken.None)).Success);
        DesktopSessionControlResponse status=null!;
        for(var index=0;index<100;index++)
        {
            status=await control.HandleAsync(request with {Operation="automation.status"},CancellationToken.None);
            if(status.RunOutcome=="Succeeded")break;
            await Task.Delay(10);
        }
        Assert.Equal("Succeeded",status.RunOutcome); Assert.Equal(1,host.Runs);
    }
    [Fact]
    public async Task ApprovedTaskReplaysOnceForAnIdempotencyKey()
    {
        var settings = new Settings(); var host = new Host(); var task = Sample(settings);
        task = task with { McpApprovalHash = DesktopTaskPackagePolicy.DefinitionHash(task) };
        settings.Current = settings.Current with { DesktopSessions = settings.Current.DesktopSessions with { Tasks = [task] } };
        using var service = new DesktopSessionApplicationService(() => settings.Current,new Ledger(),host);
        using var control = new DesktopAutomationControlHandler(settings,service);
        var request = new DesktopSessionControlRequest(1,"automation.run",task.EnvironmentId,task.Id,Guid.NewGuid());
        Assert.True((await control.HandleAsync(request,CancellationToken.None)).Success);
        Assert.True((await control.HandleAsync(request,CancellationToken.None)).Success);
        for(var i=0;i<100;i++)
        {
            var status = await control.HandleAsync(request with { Operation="automation.status" },CancellationToken.None);
            if(status.RunOutcome=="Succeeded") break;
            await Task.Delay(10);
        }
        Assert.Equal(1,host.Runs);
    }
    private static DesktopSessionTask Sample(Settings settings) => new(1,Guid.NewGuid(),"task",settings.Current.DesktopSessions.Environments[0].Id,[],new(TaskTriggerKind.Manual,null,null),1,true)
        { McpEnvironmentBinding=DesktopTaskPackagePolicy.EnvironmentBinding(settings.Current.DesktopSessions.Environments[0]) };
    private sealed class Settings : ISettingsCoordinator
    {
        public AppSettings Current { get; set; } = AppSettings.CreateDefault() with { DesktopSessions = new(true,[EnvironmentProfile.CreateDefault() with
            {Id=Guid.NewGuid(),Name="local",BackendProfileId="local-child"}],[]) };
        public event Action<AppSettings>? Changed { add {} remove {} }
        public Task InitializeAsync(CancellationToken token)=>Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings,AppSettings> change,CancellationToken token)
        { Current=change(Current); return Task.FromResult(OperationResult.Ok()); }
    }
    private sealed class Ledger : IDesktopTaskExecutionLedger
    { public Task<bool> TryBeginAsync(string key,CancellationToken token)=>Task.FromResult(true); }
    private sealed class Host : IDesktopSessionExecutionHost
    {
        public int Runs;
        public bool IsEnvironmentReady(Guid id)=>true;
        public Guid? GetLoginInstanceId(Guid id)=>Guid.NewGuid();
        public SessionRuntimeStatus GetStatus(Guid id)=>SessionRuntimeStatus.Ready;
        public Task<DesktopSessionActionStatus> StartEnvironmentAsync(EnvironmentProfile env,CancellationToken token)=>Task.FromResult(new DesktopSessionActionStatus(true,"ok"));
        public Task<DesktopSessionActionStatus> StopEnvironmentAsync(Guid id,CancellationToken token)=>Task.FromResult(new DesktopSessionActionStatus(true,"ok"));
        public Task<DesktopTaskRunResult> RunTaskAsync(DesktopSessionTask task,Guid id,CancellationToken token)
        { Interlocked.Increment(ref Runs); return Task.FromResult(new DesktopTaskRunResult(id,task.Id,DesktopTaskRunOutcome.Succeeded,task.Steps.Count,null,null)); }
        public Task<bool> CancelRunAsync(Guid id,CancellationToken token)=>Task.FromResult(true);
    }
}
