using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static class UserDataPolicy
{
    public static AppSettings Exportable(AppSettings value)
    {
        var local = value.DesktopSessions.Environments.Where(environment => environment.Backend == DesktopSessionBackendKind.LocalChild).ToArray();
        var localIds = local.Select(environment => environment.Id).Append(GlobalTaskTarget.Id).ToHashSet();
        return value with
        {
            HyperVAutomation = new(),
            DesktopSessions = value.DesktopSessions with
            {
                Environments = local,
                Tasks = value.DesktopSessions.Tasks.Where(task => localIds.Contains(task.EnvironmentId)).ToArray(),
                HyperV = null, AutomationClients = [], McpAllowedEnvironments = []
            }
        };
    }

    public static AppSettings MergeOrdinary(AppSettings incoming, AppSettings current)
    {
        var machines = current.DesktopSessions.Environments.Where(environment => environment.Backend == DesktopSessionBackendKind.HyperV).ToArray();
        var machineIds = machines.Select(environment => environment.Id).ToHashSet();
        var local = incoming.DesktopSessions.Environments.Where(environment => environment.Backend == DesktopSessionBackendKind.LocalChild).ToArray();
        if (local.Any(environment => machineIds.Contains(environment.Id))) throw new InvalidDataException("导入的本地分身 ID 与现有虚拟机冲突。");
        var localIds = local.Select(environment => environment.Id).Append(GlobalTaskTarget.Id).ToHashSet();
        var machineTasks = current.DesktopSessions.Tasks.Where(task => machineIds.Contains(task.EnvironmentId)).ToArray();
        var tasks = incoming.DesktopSessions.Tasks.Where(task => localIds.Contains(task.EnvironmentId))
            .Select(task => task with { Enabled = false, McpApprovalHash = null, McpEnvironmentBinding = null }).ToArray();
        if (tasks.Any(task => machineTasks.Any(existing => existing.Id == task.Id))) throw new InvalidDataException("导入任务 ID 与虚拟机任务冲突。");
        var available = machines.Concat(local).Select(environment => environment.Id).Append(GlobalTaskTarget.Id).ToHashSet();
        return incoming with
        {
            Assistant = incoming.Assistant with { Enabled = false, AllowComputerControl = false, AllowTaskManagement = false, AllowAssetEditing = false, AllowPrivateEndpoint = false,
                EnabledAssetIds = [], Library = incoming.Assistant.Library with { ExtractCandidates = false, EmbeddingEnabled = false }, Voice = incoming.Assistant.Voice with { Enabled = false, AutoSend = false } },
            HyperVAutomation = current.HyperVAutomation,
            DesktopSessions = current.DesktopSessions with
            {
                Enabled = machines.Length > 0 ? current.DesktopSessions.Enabled || incoming.DesktopSessions.Enabled : incoming.DesktopSessions.Enabled,
                Environments = machines.Concat(local).ToArray(), Tasks = machineTasks.Concat(tasks).ToArray(),
                LocalChild = incoming.DesktopSessions.LocalChild,
                McpAllowedEnvironments = current.DesktopSessions.McpAllowedEnvironments.Where(available.Contains).ToArray()
            }
        };
    }
}
