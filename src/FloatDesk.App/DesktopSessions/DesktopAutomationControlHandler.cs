using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

public sealed class DesktopAutomationControlHandler(ISettingsCoordinator settings, DesktopSessionApplicationService service, AutomationConnectionRegistry? connections=null) : IDesktopSessionControlHandler, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<DesktopSessionControlResponse> HandleAsync(DesktopSessionControlRequest request, CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        if (!request.Operation.StartsWith("automation.", StringComparison.Ordinal)) return await service.HandleAsync(request,token).ConfigureAwait(false);
        try
        {
            if(request.Operation=="automation.connect")
                return connections is null?DesktopSessionControlResponse.Fail("ConnectionRegistrationUnavailable","此宿主不支持连接注册。"):await connections.RegisterAsync(request,token).ConfigureAwait(false);
            if(request.Operation=="automation.connection-status")return connections?.Status(request)??DesktopSessionControlResponse.Fail("ConnectionRegistrationUnavailable","此宿主不支持连接注册。");
            var client=connections?.AuthorizedClient(request);
            if(connections is not null&&client is null)
                return DesktopSessionControlResponse.Fail("ConnectionNotTrusted","请在月语弹窗或“自动任务 → 外部 agent”中开启此连接。");
            var current = settings.Current.DesktopSessions;
            if (request.Operation == "automation.list") return Reply() with
            {
                Environments = GlobalTaskTarget.All(current).Select(env => new DesktopSessionControlEnvironment(env.Id,env.Name,env.Id==GlobalTaskTarget.Id?"Global":env.Backend.ToString())
                { McpControlAllowed = client is not null||current.McpAllowedEnvironments.Contains(env.Id) }).ToArray(),
                Tasks = current.Tasks.Select(task => new DesktopSessionControlTask(task.Id,task.Name,task.EnvironmentId,task.Enabled)
                { Revision=task.Revision, Source=task.Source, McpApproved=client is not null||(DesktopTaskPackagePolicy.IsApproved(task) &&
                    GlobalTaskTarget.Find(current,task.EnvironmentId) is {} env&&task.McpEnvironmentBinding==DesktopTaskPackagePolicy.EnvironmentBinding(env)) }).ToArray()
            };
            if (request.Operation == "automation.get") return Reply() with { TaskDefinition = current.Tasks.SingleOrDefault(task => task.Id==request.TaskId)
                ?? throw new InvalidOperationException("TaskNotFound") };
            if (request.Operation is "automation.save" or "automation.validate")
            {
                var draft = request.TaskDefinition ?? throw new InvalidOperationException("TaskMissing");
                var environment = GlobalTaskTarget.Find(current,draft.EnvironmentId) ?? throw new InvalidOperationException("EnvironmentNotFound");
                DesktopTaskPackagePolicy.Validate(draft,environment.Backend);
                if (request.Operation=="automation.validate") return Reply();
                DesktopSessionTask? saved=null;
                string? saveError=null;
                var result = await settings.UpdateAsync(latest =>
                {
                    var env = GlobalTaskTarget.Find(latest.DesktopSessions,draft.EnvironmentId) ?? throw new InvalidOperationException("EnvironmentNotFound");
                    DesktopTaskPackagePolicy.Validate(draft,env.Backend);
                    var old = latest.DesktopSessions.Tasks.SingleOrDefault(task=>task.Id==draft.Id);
                    if ((old is not null && request.ExpectedRevision!=old.Revision) || (old is null && request.ExpectedRevision is not null))
                    { saveError="TaskRevisionConflict"; throw new InvalidOperationException(saveError); }
                    var revision=old is null?1:checked(old.Revision+1);
                    saved = client is null?DesktopTaskPackagePolicy.AsExternalDraft(draft,"mcp") with {Revision=revision}:
                        draft with {Revision=revision,Source="mcp:"+client.Id,McpApprovalHash=null,McpEnvironmentBinding=DesktopTaskPackagePolicy.EnvironmentBinding(env)};
                    if(client is not null)saved=saved with {McpApprovalHash=DesktopTaskPackagePolicy.DefinitionHash(saved)};
                    return latest with { DesktopSessions=latest.DesktopSessions with
                    { Tasks=old is null ? [.. latest.DesktopSessions.Tasks,saved] : latest.DesktopSessions.Tasks.Select(task=>task.Id==saved.Id?saved:task).ToArray() } };
                },token).ConfigureAwait(false);
                return result.Success ? Reply() with { TaskDefinition=saved } : DesktopSessionControlResponse.Fail(saveError??"TaskSaveFailed","任务未保存；修订冲突时请重新读取再合并。");
            }
            if (request.Operation=="automation.status")
            {
                lock(_sync) return request.RunId is {} id && _jobs.TryGetValue(id,out var job) ? job.Response : DesktopSessionControlResponse.Fail("RunNotFound","未找到运行记录。");
            }
            if (request.Operation=="automation.cancel")
            {
                Job? job; lock(_sync) _jobs.TryGetValue(request.RunId??Guid.Empty,out job);
                if(job is null) return DesktopSessionControlResponse.Fail("RunNotFound","未找到运行记录。");
                try { job.Cancellation.Cancel(); } catch(ObjectDisposedException) { }
                return Reply() with { RunId=request.RunId,RunOutcome="CancelRequested" };
            }
            if(request.Operation is not ("automation.run" or "automation.step")) return DesktopSessionControlResponse.Fail("UnsupportedOperation","不支持此自动化操作。");
            var target=GlobalTaskTarget.Find(current,request.EnvironmentId) ?? throw new InvalidOperationException("EnvironmentNotFound");
            if(target.Id!=GlobalTaskTarget.Id&&!DesktopSessionModePolicy.IsEnabled(current,target.Backend)) throw new InvalidOperationException("BackendModeDisabled");
            DesktopSessionTask taskToRun;
            if(request.Operation=="automation.step")
            {
                if(client is null&&!current.McpAllowedEnvironments.Contains(target.Id)) return DesktopSessionControlResponse.Fail("NeedsAuthorization","请在本地允许 MCP 控制此环境。");
                var step=request.Step ?? throw new InvalidOperationException("StepMissing");
                if(client is null&&step.Kind is SessionCommandKind.RunScript or SessionCommandKind.RunSystemScript or SessionCommandKind.CaptureScreenshot)
                    return DesktopSessionControlResponse.Fail("TaskApprovalRequired","脚本须保存为任务并经本地审核；截图请用 ObserveDesktop。");
                taskToRun=new(1,request.RunId??Guid.Empty,"MCP interactive",target.Id,[step],new(TaskTriggerKind.Manual,null,null),1,true);
            }
            else
            {
                taskToRun=current.Tasks.SingleOrDefault(task=>task.Id==request.TaskId && task.EnvironmentId==target.Id) ?? throw new InvalidOperationException("TaskNotFound");
                if(client is null&&(!DesktopTaskPackagePolicy.IsApproved(taskToRun) || taskToRun.McpEnvironmentBinding!=DesktopTaskPackagePolicy.EnvironmentBinding(target)))
                    return DesktopSessionControlResponse.Fail("NeedsAuthorization","请在本地审核并授权当前版本任务。");
                if(!taskToRun.Enabled) throw new InvalidOperationException("TaskDisabled");
            }
            DesktopTaskPackagePolicy.Validate(taskToRun,target.Backend);
            var runId=request.RunId is {} requested && requested!=Guid.Empty ? requested : throw new InvalidOperationException("IdempotencyKeyRequired");
            var hash=DesktopTaskPackagePolicy.DefinitionHash(taskToRun);
            lock(_sync)
            {
                if(_jobs.TryGetValue(runId,out var existing)) return existing.Hash==hash ? existing.Response : DesktopSessionControlResponse.Fail("IdempotencyConflict","此运行 ID 已用于另一任务定义。");
                foreach(var key in _jobs.Where(pair=>pair.Value.Completed).Select(pair=>pair.Key).Take(Math.Max(0,_jobs.Count-31)).ToArray()) _jobs.Remove(key);
                if(_jobs.Count>=32) throw new InvalidOperationException("RunQueueFull");
                var job=new Job(hash,CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),Reply() with {RunId=runId,RunOutcome="Running"});
                _jobs.Add(runId,job);
                _=Task.Run(async()=>
                {
                    try
                    {
                        var result=await service.RunAsync(taskToRun,runId,job.Cancellation.Token).ConfigureAwait(false);
                        job.Response=Reply() with { Success=result.Outcome==DesktopTaskRunOutcome.Succeeded,ErrorCode=result.ErrorCode,
                            RunId=runId,RunOutcome=result.Outcome.ToString(),CompletedSteps=result.CompletedSteps,FailedStepId=result.FailedStepId,DataJson=result.DataJson };
                    }
                    catch(OperationCanceledException) {job.Response=Reply() with {Success=false,RunId=runId,RunOutcome="Cancelled"};}
                    catch(Exception) {job.Response=DesktopSessionControlResponse.Fail("RunFailed","任务执行未完成。") with {RunId=runId,RunOutcome="Failed"};}
                    finally {job.Completed=true;job.Cancellation.Dispose();}
                }, CancellationToken.None);
                return job.Response;
            }
        }
        catch(Exception exception) when(exception is InvalidOperationException or ArgumentException)
        { return DesktopSessionControlResponse.Fail(exception.Message.Length<80?exception.Message:"InvalidTask","自动化请求未通过校验。"); }
    }
    public void Dispose()=>_lifetime.Cancel();
    private static DesktopSessionControlResponse Reply()=>new(true,null,null,null,null,null,null,null,null);
    private sealed class Job(string hash,CancellationTokenSource cancellation,DesktopSessionControlResponse response)
    {
        public string Hash { get; }=hash;
        public CancellationTokenSource Cancellation {get;}=cancellation;
        public volatile DesktopSessionControlResponse Response=response;
        public volatile bool Completed;
    }
}
