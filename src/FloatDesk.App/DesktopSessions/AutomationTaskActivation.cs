using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Threading.Channels;

namespace FloatDesk.App.DesktopSessions;

public sealed class AutomationTaskActivation : IDisposable
{
    private readonly ISettingsCoordinator _settings;
    private readonly DesktopSessionApplicationService _service;
    private readonly Channel<DesktopSessionTask> _queue=Channel.CreateUnbounded<DesktopSessionTask>(new(){SingleReader=true});
    private readonly CancellationTokenSource _lifetime=new();
    private readonly object _sync=new();
    private Dictionary<Guid,DesktopSessionTask>? _previous;
    private readonly Task _worker;
    public AutomationTaskActivation(ISettingsCoordinator settings,DesktopSessionApplicationService service)
    { _settings=settings;_service=service;settings.Changed+=Changed;_worker=Task.Run(WorkAsync); }
    private void Changed(AppSettings settings)
    {
        lock(_sync)
        {
            if(_previous is not null)
                foreach(var task in settings.DesktopSessions.Tasks.Where(task=>task.Enabled&&(!_previous.TryGetValue(task.Id,out var old)||!old.Enabled)))
                    _queue.Writer.TryWrite(task);
            _previous=settings.DesktopSessions.Tasks.ToDictionary(task=>task.Id);
        }
    }
    private async Task WorkAsync()
    {
        try
        {
            await foreach(var task in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var current=_settings.Current.DesktopSessions.Tasks.FirstOrDefault(item=>item.Id==task.Id);
                if(current is null||!current.Enabled||current.Revision!=task.Revision)continue;
                try
                {
                    if(task.Trigger.Kind==TaskTriggerKind.Manual)await _service.RunAsync(task,Guid.NewGuid(),_lifetime.Token).ConfigureAwait(false);
                    else if(task.Trigger.Kind==TaskTriggerKind.EnvironmentReady&&_service.IsEnvironmentReady(task.EnvironmentId))
                        await _service.ProcessEnvironmentReadyAsync(task.EnvironmentId,_lifetime.Token).ConfigureAwait(false);
                }
                catch(Exception exception) when(exception is not OperationCanceledException) { }
            }
        }
        catch(OperationCanceledException) { }
    }
    public void Dispose(){_settings.Changed-=Changed;_lifetime.Cancel();_queue.Writer.TryComplete();}
}
