using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;
using System.Collections.ObjectModel;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    private bool _loadingDesktopSessions;
    private bool _desktopSessionsEdited;

    [ObservableProperty]
    private bool _desktopSessionsEnabled;

    public ObservableCollection<EnvironmentProfile>
        DesktopSessionEnvironments { get; } = [];

    public ObservableCollection<DesktopSessionTask>
        DesktopSessionTasks { get; } = [];

    public bool DesktopSessionsEdited => _desktopSessionsEdited;

    public DesktopSessionSettings BuildDesktopSessionDraft() =>
        new(
            DesktopSessionsEnabled,
            DesktopSessionEnvironments.ToArray(),
            DesktopSessionTasks.ToArray());

    public void ReplaceDesktopSessionSettings(
        DesktopSessionSettings settings,
        bool markEdited = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LoadDesktopSessionCollections(settings);
        if (markEdited)
            _desktopSessionsEdited = true;
    }

    public void AddDesktopSessionEnvironment(EnvironmentProfile environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (DesktopSessionEnvironments.Any(
                existing => existing.Id == environment.Id))
        {
            throw new InvalidOperationException(
                "桌面分身环境 ID 已存在。");
        }

        DesktopSessionEnvironments.Add(environment);
        MarkDesktopSessionsEdited();
    }

    public void UpdateDesktopSessionEnvironment(
        Guid environmentId,
        Func<EnvironmentProfile, EnvironmentProfile> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var environments = DesktopSessionEnvironments.ToArray();
        var index = Array.FindIndex(
            environments,
            environment => environment.Id == environmentId);
        if (index < 0)
        {
            throw new InvalidOperationException(
                "未找到桌面分身环境。");
        }

        var edited = update(environments[index]) ??
            throw new InvalidOperationException(
                "桌面分身环境编辑结果为空。");
        if (edited.Id != environmentId)
        {
            throw new InvalidOperationException(
                "桌面分身环境 ID 不可更改。");
        }

        DesktopSessionEnvironments[index] = edited;
        MarkDesktopSessionsEdited();
    }

    public void RemoveDesktopSessionEnvironment(Guid environmentId)
    {
        var environment = DesktopSessionEnvironments.FirstOrDefault(
            item => item.Id == environmentId);
        if (environment is null)
            return;
        DesktopSessionEnvironments.Remove(environment);
        foreach (var task in DesktopSessionTasks
                     .Where(task => task.EnvironmentId == environmentId)
                     .ToArray())
        {
            DesktopSessionTasks.Remove(task);
        }

        MarkDesktopSessionsEdited();
    }

    public void AddDesktopSessionTask(DesktopSessionTask task)
    {
        var updated = DesktopSessionConfigurationEditor.AddTask(
            BuildDesktopSessionDraft(),
            task);
        ReplaceTaskCollection(updated);
        MarkDesktopSessionsEdited();
    }

    public void UpdateDesktopSessionTask(
        Guid taskId,
        Func<DesktopSessionTask, DesktopSessionTask> update)
    {
        var updated = DesktopSessionConfigurationEditor.UpdateTask(
            BuildDesktopSessionDraft(),
            taskId,
            update);
        ReplaceTaskCollection(updated);
        MarkDesktopSessionsEdited();
    }

    public void RemoveDesktopSessionTask(Guid taskId)
    {
        var task = DesktopSessionTasks.FirstOrDefault(
            item => item.Id == taskId);
        if (task is not null)
            DesktopSessionTasks.Remove(task);
        MarkDesktopSessionsEdited();
    }

    public DesktopSessionSettings MergeDesktopSessions(
        DesktopSessionSettings latest) =>
        _desktopSessionsEdited
            ? BuildDesktopSessionDraft()
            : latest;

    internal void LoadDesktopSessions(DesktopSessionSettings settings)
    {
        LoadDesktopSessionCollections(settings);
        _desktopSessionsEdited = false;
    }

    partial void OnDesktopSessionsEnabledChanged(bool value)
    {
        if (!_loadingDesktopSessions)
            MarkDesktopSessionsEdited();
    }

    private void LoadDesktopSessionCollections(
        DesktopSessionSettings settings)
    {
        _loadingDesktopSessions = true;
        try
        {
            DesktopSessionsEnabled = settings.Enabled;
            DesktopSessionEnvironments.Clear();
            foreach (var environment in settings.Environments)
                DesktopSessionEnvironments.Add(environment);
            DesktopSessionTasks.Clear();
            foreach (var task in settings.Tasks)
                DesktopSessionTasks.Add(task);
        }
        finally
        {
            _loadingDesktopSessions = false;
        }
    }

    private void ReplaceTaskCollection(
        DesktopSessionSettings updated)
    {
        DesktopSessionTasks.Clear();
        foreach (var task in updated.Tasks)
            DesktopSessionTasks.Add(task);
    }

    private void MarkDesktopSessionsEdited() =>
        _desktopSessionsEdited = true;
}
