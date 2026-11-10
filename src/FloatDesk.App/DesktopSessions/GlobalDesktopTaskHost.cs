using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatDesk.App.DesktopSessions;

public sealed class GlobalDesktopTaskHost : IDesktopSessionExecutionHost, IDesktopSessionExecutionShutdown, IDisposable,
    IDesktopSessionTransport, IDesktopTaskResolutionProvider
{
    private readonly IDesktopSessionExecutionHost _inner;
    private readonly IBrightnessService _brightness;
    private readonly JobObjectScriptRunner _scripts=new();
    private readonly AutomationStepExecutor _executor;
    private readonly DesktopTaskRunner _runner;
    private readonly SessionHandle _handle=new(GlobalTaskTarget.Id,Guid.NewGuid(),(uint)Process.GetCurrentProcess().SessionId,Guid.NewGuid(),true);
    private readonly CancellationTokenSource _lifetime=new();
    private bool _disposed;

    public GlobalDesktopTaskHost(IDesktopSessionExecutionHost inner,IBrightnessService brightness)
    {
        _inner=inner;_brightness=brightness;
        _executor=new(new WindowsSessionAutomationBackend(),_scripts,new SystemClock());
        _runner=new(this,this,new SystemClock());
    }
    public bool IsEnvironmentReady(Guid environmentId)=>environmentId==GlobalTaskTarget.Id? !_disposed&&DesktopInputDiagnostics.IsReady():_inner.IsEnvironmentReady(environmentId);
    public Guid? GetLoginInstanceId(Guid environmentId)=>environmentId==GlobalTaskTarget.Id?_handle.LoginInstanceId:_inner.GetLoginInstanceId(environmentId);
    public SessionRuntimeStatus GetStatus(Guid environmentId)=>environmentId==GlobalTaskTarget.Id?(IsEnvironmentReady(environmentId)?SessionRuntimeStatus.Ready:SessionRuntimeStatus.Paused):_inner.GetStatus(environmentId);
    public Task<DesktopSessionActionStatus> StartEnvironmentAsync(EnvironmentProfile environment,CancellationToken cancellationToken)=>environment.Id==GlobalTaskTarget.Id?
        Task.FromResult(new DesktopSessionActionStatus(IsEnvironmentReady(environment.Id),"全局任务使用当前主桌面。")):_inner.StartEnvironmentAsync(environment,cancellationToken);
    public Task<DesktopSessionActionStatus> StopEnvironmentAsync(Guid environmentId,CancellationToken cancellationToken)=>environmentId==GlobalTaskTarget.Id?
        Task.FromResult(new DesktopSessionActionStatus(false,"全局任务不注销主桌面，请停止具体任务。")):_inner.StopEnvironmentAsync(environmentId,cancellationToken);
    public async Task<DesktopTaskRunResult> RunTaskAsync(DesktopSessionTask task,Guid runId,CancellationToken cancellationToken)
    {
        if(task.EnvironmentId!=GlobalTaskTarget.Id)return await _inner.RunTaskAsync(task,runId,cancellationToken).ConfigureAwait(false);
        if(!IsEnvironmentReady(task.EnvironmentId))return new(runId,task.Id,DesktopTaskRunOutcome.Failed,0,null,"MainDesktopNotReady");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_lifetime.Token);
        try{return await _runner.RunAsync(_handle,task,runId,linked.Token).ConfigureAwait(false);}
        finally{await _executor.ReleaseHeldInputAsync(CancellationToken.None).ConfigureAwait(false);}
    }
    public async Task<bool> CancelRunAsync(Guid runId,CancellationToken cancellationToken)=>await _runner.CancelRunAsync(runId,cancellationToken).ConfigureAwait(false)||await _inner.CancelRunAsync(runId,cancellationToken).ConfigureAwait(false);
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();await _executor.ReleaseHeldInputAsync(cancellationToken).ConfigureAwait(false);
        if(_inner is IDesktopSessionExecutionShutdown shutdown)await shutdown.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_lifetime.Cancel();_scripts.Dispose();(_inner as IDisposable)?.Dispose();}
    public Task<SessionTransportHandshake> ConnectAsync(SessionHandle handle,CancellationToken cancellationToken)=>Task.FromResult(new SessionTransportHandshake(handle==_handle,_handle.Generation,_handle.LoginInstanceId,handle==_handle?null:"WrongGlobalTarget"));
    public Task AcquireInputLeaseAsync(SessionHandle handle,TimeSpan leaseDuration,CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task RenewInputLeaseAsync(SessionHandle handle,CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task ReleaseInputLeaseAsync(SessionHandle handle,CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task CancelAsync(SessionHandle handle,Guid commandId,CancellationToken cancellationToken)=>_executor.ReleaseHeldInputAsync(cancellationToken);
    public Task<DesktopSessionResolution> GetResolutionAsync(SessionHandle handle,CancellationToken cancellationToken)=>Task.FromResult(new DesktopSessionResolution(GetSystemMetrics(0),GetSystemMetrics(1)));
    public async Task<CommandResult> SendAsync(SessionHandle handle,SessionCommand command,CancellationToken cancellationToken)
    {
        if(handle!=_handle||command.Handle!=_handle||!IsEnvironmentReady(GlobalTaskTarget.Id))return new(command.CommandId,false,false,"WrongGlobalTargetOrLocked");
        if(command.Kind==SessionCommandKind.SetBrightness)
        {
            if(!command.Parameters.TryGetValue("percent",out var value)||!int.TryParse(value,out var percent)||percent is <0 or >100)return new(command.CommandId,false,false,"BrightnessPercentInvalid");
            var snapshot=await _brightness.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var targets=snapshot.Devices.Where(device=>device.Percent is not null).ToArray();
            if(targets.Length==0)return new(command.CommandId,false,false,"BrightnessUnavailable");
            foreach(var device in targets)
            {
                var result=await _brightness.SetPercentAsync(device.Id,percent,cancellationToken).ConfigureAwait(false);
                if(result.Error is not null)return new(command.CommandId,false,false,"BrightnessWriteFailed");
            }
            return new(command.CommandId,true,false,null);
        }
        if(command.Kind==SessionCommandKind.RunSystemScript)return new(command.CommandId,false,false,"GuestAdminRequiresHyperV");
        return await _executor.ExecuteAsync(command,cancellationToken).ConfigureAwait(false);
    }
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
}
