using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopTaskMergeTests
{
    [Fact]
    public void LiveRefreshShowsNewAgentTasksWithoutOverwritingUnsavedLocalRules()
    {
        var environment=EnvironmentProfile.CreateDefault() with {Id=Guid.NewGuid(),Name="env"};
        var task=new DesktopSessionTask(1,Guid.NewGuid(),"local",environment.Id,[],new(TaskTriggerKind.Manual,null,null),1,false);
        var original=AppSettings.CreateDefault() with {DesktopSessions=new(false,[environment],[task])};
        var model=SettingsViewModel.From(original);model.UpdateDesktopSessionTask(task.Id,item=>item with {Rules="unsaved local"});
        var added=task with {Id=Guid.NewGuid(),Name="agent"};
        model.MergeExternalTaskUpdates(original.DesktopSessions with {Tasks=[task with {Revision=2,Rules="external"},added]});
        Assert.Equal(2,model.DesktopSessionTasks.Count);
        Assert.Equal("unsaved local",model.DesktopSessionTasks.Single(item=>item.Id==task.Id).Rules);
        Assert.Throws<InvalidOperationException>(()=>model.MergeDesktopSessions(original.DesktopSessions with {Tasks=[task with {Revision=2,Rules="external"},added]}));
    }
    [Fact]
    public void ApplyingAnEnvironmentEditPreservesNewMcpDrafts()
    {
        var environment=EnvironmentProfile.CreateDefault() with {Id=Guid.NewGuid(),Name="env"};
        var original=AppSettings.CreateDefault() with {DesktopSessions=new(true,[environment],[])};
        var viewModel=SettingsViewModel.From(original);
        viewModel.UpdateDesktopSessionEnvironment(environment.Id,item=>item with {Name="renamed"});
        var external=new DesktopSessionTask(1,Guid.NewGuid(),"external",environment.Id,[],new(TaskTriggerKind.Manual,null,null),1,false) {Rules="agent rules",Source="mcp"};
        var merged=viewModel.MergeDesktopSessions(original.DesktopSessions with {Tasks=[external]});
        Assert.Equal(external,Assert.Single(merged.Tasks));
    }
    [Fact]
    public void ConcurrentEditsToTheSameTaskRequireReload()
    {
        var environment=EnvironmentProfile.CreateDefault() with {Id=Guid.NewGuid(),Name="env"};
        var task=new DesktopSessionTask(1,Guid.NewGuid(),"task",environment.Id,[],new(TaskTriggerKind.Manual,null,null),1,true);
        var original=AppSettings.CreateDefault() with {DesktopSessions=new(true,[environment],[task])};
        var viewModel=SettingsViewModel.From(original);
        viewModel.UpdateDesktopSessionTask(task.Id,item=>item with {Rules="local edit"});
        var latest=original.DesktopSessions with {Tasks=[task with {Revision=2,Rules="external edit"}]};
        Assert.Throws<InvalidOperationException>(()=>viewModel.MergeDesktopSessions(latest));
    }
}
