using System.Runtime.CompilerServices;
using System.Text.Json;
using FloatDesk.App.Assistant;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantWorkspaceRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoTargetNeverDefaultsToMainAndAlwaysConfirmsAnExplicitTarget(bool suppliesTarget)
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-AutoTarget-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root,"assistant.db")); var target = Guid.NewGuid();
            var call = new AssistantToolCall("call", "application_launch", suppliesTarget ? JsonSerializer.Serialize(new { environmentId = target }) : "{}");
            var model = new Model(call); var tools = new Tools(); var settings = new AssistantSettings { Enabled = true, Endpoint = "https://example.test/v1", Model = "test", SupportsTools = true, AllowComputerControl = true };
            using var runtime = new AssistantRuntime(() => settings, store, model, new Credentials(), tools); runtime.SetTarget(Guid.Empty); var approvals = 0;
            runtime.ConfirmTool = (preview, _) => { Assert.True(preview.AlwaysConfirm); Assert.Equal(target, preview.EnvironmentId); approvals++; return Task.FromResult(true); };
            var session = await runtime.CreateSessionAsync("auto", CancellationToken.None); await runtime.SendAsync(session.Id,"在合适的分身执行",CancellationToken.None);
            Assert.Equal(suppliesTarget ? 1 : 0, tools.Executions); Assert.Equal(suppliesTarget ? 1 : 0, approvals);
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelCanSaveRoleTextOnlyAfterExplicitApproval(bool approve)
    {
        var root = Path.Combine(Path.GetTempPath(),"YueYu-ModelAsset-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var store=new AssistantStore(Path.Combine(root,"assistant.db"));
            var settings=new AssistantSettings { Enabled=true,Endpoint="https://example.test/v1",Model="test",SupportsTools=true,AllowAssetEditing=true };
            var model=new Model(new("call","asset_save","{\"name\":\"helper\",\"kind\":\"role\",\"text\":\"Be concise.\"}"));
            using var runtime=new AssistantRuntime(()=>settings,store,model,new Credentials());
            runtime.ConfirmTool=(preview,_)=> { Assert.Equal("assets",preview.Category); Assert.Contains("Be concise.",preview.Description); return Task.FromResult(approve); };
            var session=await runtime.CreateSessionAsync("asset",CancellationToken.None); await runtime.SendAsync(session.Id,"保存角色",CancellationToken.None);
            Assert.Equal(approve?1:0,(await store.ListAssetsAsync(CancellationToken.None)).Count); Assert.Empty(settings.EnabledAssetIds);
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Fact]
    public async Task MentionedSkillIsTransientAndLegacyMultipleRolesAreReducedToOne()
    {
        var root=Path.Combine(Path.GetTempPath(),"YueYu-Mention-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var store=new AssistantStore(Path.Combine(root,"assistant.db"));
            var a=new AssistantAsset(Guid.NewGuid(),"A","role","ROLE_A","manual",new string('a',64)); var b=a with { Id=Guid.NewGuid(),Text="ROLE_B" }; var skill=a with { Id=Guid.NewGuid(),Kind="skill",Text="MENTION_ONLY" };
            foreach(var asset in new[]{a,b,skill}) await store.SaveAssetAsync(asset,CancellationToken.None);
            var settings=new AssistantSettings { Enabled=true,Endpoint="https://example.test/v1",Model="test",EnabledAssetIds=[a.Id,b.Id] }; var model=new Model(null);
            using var runtime=new AssistantRuntime(()=>settings,store,model,new Credentials()); runtime.ConfirmOutbound=(_,_)=>Task.FromResult(true);
            var session=await runtime.CreateSessionAsync("mention",CancellationToken.None); await runtime.SendAsync(session.Id,"使用技能",CancellationToken.None,[skill.Id]);
            var sent=string.Join('\n',model.Last!.Messages.Select(message=>message.Content)); Assert.Contains("ROLE_B",sent); Assert.DoesNotContain("ROLE_A",sent); Assert.Contains("MENTION_ONLY",sent); Assert.DoesNotContain(skill.Id,settings.EnabledAssetIds);
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class Model(AssistantToolCall? call) : IAssistantModelClient
    {
        private int _requests; public AssistantChatRequest? Last {get;private set;}
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request,[EnumeratorCancellation] CancellationToken token)
        {
            Last=request; await Task.Yield();token.ThrowIfCancellationRequested();
            if(_requests++==0 && call is not null) yield return new(ToolCall:new(0,call.Id,call.Name,call.Arguments)); else yield return new("done");
            yield return new(IsComplete:true);
        }
    }
    private sealed class Tools : IAssistantToolExecutor
    {
        public int Executions{get;private set;}
        public IReadOnlyList<AssistantToolSchema> GetSchemas()=>[];
        public AssistantToolPreview Preview(AssistantToolCall call,Guid target)=>new(call,target,"binding","target","launch","control",false);
        public Task<AssistantToolResult> ExecuteAsync(AssistantToolPreview preview,CancellationToken token){Executions++;return Task.FromResult(new AssistantToolResult(true,"done"));}
    }
    private sealed class Credentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose,string endpoint,CancellationToken token)=>Task.FromResult<string?>(null);
        public Task SaveAsync(string purpose,string endpoint,string secret,CancellationToken token)=>Task.CompletedTask;
        public Task DeleteAsync(string purpose,string endpoint,CancellationToken token)=>Task.CompletedTask;
    }
}
