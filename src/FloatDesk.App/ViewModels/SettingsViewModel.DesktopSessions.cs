using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;
using System.Collections.ObjectModel;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    private bool _loadingDesktopSessions;
    private bool _desktopSessionsEdited;
    private readonly Dictionary<Guid, (int Width, int Height)> _loadedDesktopResolutions = [];
    private readonly Dictionary<Guid,DesktopSessionTask> _loadedDesktopTasks = [];

    [ObservableProperty]
    private bool _desktopSessionsEnabled;

    [ObservableProperty]
    private bool _localDesktopSessionEnabled;

    [ObservableProperty]
    private bool _hyperVDesktopSessionEnabled;

    [ObservableProperty]
    private bool _desktopSessionAdvancedSettings;

    partial void OnDesktopSessionAdvancedSettingsChanged(bool value)
    {
        if (!_loadingDesktopSessions) MarkDesktopSessionsEdited();
    }

    private Guid? _localDefaultEnvironmentId;
    private Guid? _hyperVDefaultEnvironmentId;
    private readonly HashSet<Guid> _mcpAllowedEnvironments = [];
    public bool IsMcpControlAllowed(Guid id) => _mcpAllowedEnvironments.Contains(id);
    public void SetMcpControlAllowed(Guid id, bool allowed)
    {
        if (allowed) _mcpAllowedEnvironments.Add(id); else _mcpAllowedEnvironments.Remove(id);
        MarkDesktopSessionsEdited();
    }

    public ObservableCollection<EnvironmentProfile>
        DesktopSessionEnvironments { get; } = [];

    public ObservableCollection<DesktopSessionTask>
        DesktopSessionTasks { get; } = [];

    public bool DesktopSessionsEdited => _desktopSessionsEdited;

    public DesktopSessionSettings BuildDesktopSessionDraft() =>
        DesktopSessionModePolicy.Normalize(new(
            DesktopSessionsEnabled,
            DesktopSessionEnvironments.ToArray(),
            DesktopSessionTasks.ToArray())
        {
            LocalChild = new(LocalDesktopSessionEnabled, _localDefaultEnvironmentId),
            HyperV = new(HyperVDesktopSessionEnabled, _hyperVDefaultEnvironmentId),
            ShowAdvancedSettings = DesktopSessionAdvancedSettings,
            McpAllowedEnvironments = _mcpAllowedEnvironments.Where(id => DesktopSessionEnvironments.Any(env => env.Id==id)).ToArray()
        });

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

        if (edited.Backend != environments[index].Backend || edited.BackendProfileId != environments[index].BackendProfileId)
            _mcpAllowedEnvironments.Remove(environmentId);
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

    public DesktopSessionSettings MergeDesktopSessions(DesktopSessionSettings latest)
    {
        if (!_desktopSessionsEdited)
            return latest;
        var draft = BuildDesktopSessionDraft();
        return draft with
        {
            Tasks = MergeTaskLibrary(draft,latest),
            AutomationClients=latest.AutomationClients,
            Environments = draft.Environments.Select(environment =>
            {
                var current = latest.Environments.FirstOrDefault(item => item.Id == environment.Id);
                return current is not null &&
                    _loadedDesktopResolutions.TryGetValue(environment.Id, out var loaded) &&
                    loaded == (environment.DesktopWidth, environment.DesktopHeight)
                    ? environment with { DesktopWidth = current.DesktopWidth, DesktopHeight = current.DesktopHeight }
                    : environment;
            }).ToArray()
        };
    }

    internal void LoadDesktopSessions(DesktopSessionSettings settings)
    {
        LoadDesktopSessionCollections(settings);
        _desktopSessionsEdited = false;
    }

    partial void OnDesktopSessionsEnabledChanged(bool value)
    {
        if (!_loadingDesktopSessions)
        {
            _loadingDesktopSessions = true;
            LocalDesktopSessionEnabled = value;
            HyperVDesktopSessionEnabled = value;
            _loadingDesktopSessions = false;
            MarkDesktopSessionsEdited();
        }
    }

    partial void OnLocalDesktopSessionEnabledChanged(bool value) => UpdateModeEnabled();

    partial void OnHyperVDesktopSessionEnabledChanged(bool value) => UpdateModeEnabled();

    private void UpdateModeEnabled()
    {
        if (_loadingDesktopSessions)
            return;
        _loadingDesktopSessions = true;
        DesktopSessionsEnabled = LocalDesktopSessionEnabled || HyperVDesktopSessionEnabled;
        _loadingDesktopSessions = false;
        MarkDesktopSessionsEdited();
    }

    private void LoadDesktopSessionCollections(
        DesktopSessionSettings settings)
    {
        _loadingDesktopSessions = true;
        try
        {
            settings = DesktopSessionModePolicy.Normalize(settings);
            DesktopSessionsEnabled = settings.Enabled;
            LocalDesktopSessionEnabled = settings.LocalChild!.Enabled;
            HyperVDesktopSessionEnabled = settings.HyperV!.Enabled;
            DesktopSessionAdvancedSettings = settings.ShowAdvancedSettings;
            _localDefaultEnvironmentId = settings.LocalChild.DefaultEnvironmentId;
            _hyperVDefaultEnvironmentId = settings.HyperV.DefaultEnvironmentId;
            _mcpAllowedEnvironments.Clear();
            foreach (var id in settings.McpAllowedEnvironments) _mcpAllowedEnvironments.Add(id);
            _loadedDesktopResolutions.Clear();
            _loadedDesktopTasks.Clear();
            foreach (var task in settings.Tasks) _loadedDesktopTasks[task.Id]=task;
            foreach (var environment in settings.Environments)
                _loadedDesktopResolutions[environment.Id] = (environment.DesktopWidth, environment.DesktopHeight);
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

    public void MergeExternalTaskUpdates(DesktopSessionSettings latest,Guid? protectedEditorTaskId=null)
    {
        foreach(var original in _loadedDesktopTasks.Values.ToArray())
        {
            var local=DesktopSessionTasks.FirstOrDefault(task=>task.Id==original.Id);
            if(local is null||local.Revision!=original.Revision||local.Id==protectedEditorTaskId)continue;
            var external=latest.Tasks.FirstOrDefault(task=>task.Id==original.Id);
            if(external==local)continue;
            var index=DesktopSessionTasks.IndexOf(local);
            if(external is null){DesktopSessionTasks.RemoveAt(index);_loadedDesktopTasks.Remove(original.Id);}
            else{DesktopSessionTasks[index]=external;_loadedDesktopTasks[external.Id]=external;}
        }
        foreach(var external in latest.Tasks.Where(task=>!_loadedDesktopTasks.ContainsKey(task.Id)&&!DesktopSessionTasks.Any(local=>local.Id==task.Id)))
        { DesktopSessionTasks.Add(external);_loadedDesktopTasks[external.Id]=external; }
    }

    private List<DesktopSessionTask> MergeTaskLibrary(DesktopSessionSettings draft,DesktopSessionSettings latest)
    {
        var result=new List<DesktopSessionTask>();
        foreach(var loaded in _loadedDesktopTasks.Values)
        {
            var current=latest.Tasks.FirstOrDefault(task=>task.Id==loaded.Id);
            if(!draft.Tasks.Any(task=>task.Id==loaded.Id)&&current is not null&&current.Revision!=loaded.Revision)
                throw new InvalidOperationException("此任务已由外部更新，请重新加载后再删除。");
        }
        foreach(var edited in draft.Tasks)
        {
            var current=latest.Tasks.FirstOrDefault(task=>task.Id==edited.Id);
            if(_loadedDesktopTasks.TryGetValue(edited.Id,out var loaded))
            {
                if(edited.Revision==loaded.Revision)
                { if(current is not null)result.Add(current);continue; }
                if(current is null || (current.Revision!=loaded.Revision && DesktopTaskPackagePolicy.DefinitionHash(current)!=DesktopTaskPackagePolicy.DefinitionHash(edited)))
                    throw new InvalidOperationException("任务已有外部修改，请先重新加载，避免覆盖 agent 写入的规则或脚本。");
            }
            else if(current is not null&&DesktopTaskPackagePolicy.DefinitionHash(current)!=DesktopTaskPackagePolicy.DefinitionHash(edited))
                throw new InvalidOperationException("新任务 ID 与外部任务冲突，请重新加载。");
            result.Add(edited);
        }
        result.AddRange(latest.Tasks.Where(task=>!_loadedDesktopTasks.ContainsKey(task.Id)&&!draft.Tasks.Any(edited=>edited.Id==task.Id)&&
            (task.EnvironmentId==GlobalTaskTarget.Id||draft.Environments.Any(env=>env.Id==task.EnvironmentId))));
        return result;
    }
}
