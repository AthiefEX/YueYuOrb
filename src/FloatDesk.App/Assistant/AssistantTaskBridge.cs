using System.Text.Json;
using System.Text.Json.Serialization;
using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Assistant;

public sealed class AssistantTaskBridge(ISettingsCoordinator settings,
    Func<DesktopSessionTask, Guid, CancellationToken, Task<DesktopTaskRunResult>> runTask,
    Func<Guid, bool>? isReady = null) : IAssistantToolExecutor
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    private static readonly SessionCommandKind[] DirectKinds = [SessionCommandKind.LaunchApplication, SessionCommandKind.CloseApplication, SessionCommandKind.WaitForWindow,
        SessionCommandKind.ClickElement, SessionCommandKind.TypeText, SessionCommandKind.SendKeys, SessionCommandKind.Delay, SessionCommandKind.ObserveDesktop, SessionCommandKind.SetBrightness];
    private static readonly HashSet<string> ScriptHosts = new(["powershell", "powershell_ise", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msbuild", "installutil", "wsl", "bash", "sh", "node", "deno", "python", "python3", "pythonw", "ruby", "perl", "php"], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AssistantToolSchema> GetSchemas()
    {
        var value = settings.Current.Assistant;
        if (!value.Enabled || !value.SupportsTools) return [];
        var schemas = new List<AssistantToolSchema>
        {
            Schema("environments_list", "列出可用目标；手动模式不可改变界面对象，自动模式须据此选择environmentId并在操作前由用户确认。", "{}"),
            Schema("tasks_list", "列出当前目标的已有任务标识、修订、触发器和启用状态。", "{}")
        };
        if (value.AllowComputerControl)
        {
            schemas.Add(Schema("application_launch", "在当前已选目标启动应用。必须提供该目标内的绝对路径，不可猜测路径。", "{\"path\":{\"type\":\"string\"},\"arguments\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"workingDirectory\":{\"type\":\"string\"}}", ["path"]));
            schemas.Add(Schema("application_close", "请求正常关闭当前目标中匹配完整路径的应用，不强制结束进程。", "{\"path\":{\"type\":\"string\"}}", ["path"]));
            schemas.Add(Schema("desktop_step", "对当前目标执行一个受控动作。kind支持WaitForWindow、ClickElement、TypeText、SendKeys、Delay、ObserveDesktop、SetBrightness；parameters为字符串字典。观察用mode=windows或uiTree，不支持直接截图/脚本。观察后再操作，失败不冒充成功。", "{\"kind\":{\"type\":\"string\"},\"parameters\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}},\"timeoutSeconds\":{\"type\":\"integer\"}}", ["kind", "parameters"]));
        }
        if (value.AllowTaskManagement)
        {
            schemas.Add(Schema("task_save", "创建或修改持久任务。先预览，再由用户确认保存/启用。steps中每步含kind与parameters字符串字典；LaunchApplication参数可用path、arguments(JSON数组字符串)、workingDirectory。脚本须附scripts数组{name,content}并经用户明确审核，不能直接执行SQL或系统命令。trigger含kind(Manual/EnvironmentReady/Scheduled)、localTime(HH:mm)、timeZoneId。修改已有任务必须带taskId及expectedRevision。", "{\"taskId\":{\"type\":\"string\"},\"expectedRevision\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"enabled\":{\"type\":\"boolean\"},\"trigger\":{\"type\":\"object\"},\"steps\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"scripts\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}}", ["name", "steps"]));
            schemas.Add(Schema("task_run", "运行当前目标中已启用的已有任务，使用真实执行回执。", "{\"taskId\":{\"type\":\"string\"}}", ["taskId"]));
            schemas.Add(Schema("task_set_enabled", "启停已有任务。启用Manual任务可能立即运行；用户将看到完整预览。", "{\"taskId\":{\"type\":\"string\"},\"enabled\":{\"type\":\"boolean\"},\"expectedRevision\":{\"type\":\"integer\"}}", ["taskId", "enabled", "expectedRevision"]));
        }
        return schemas;
    }

    public AssistantToolPreview Preview(AssistantToolCall request, Guid environmentId)
    {
        if (!settings.Current.Assistant.Enabled || !settings.Current.Assistant.SupportsTools) throw new InvalidOperationException("助手或工具调用未开启。");
        if (request.Id.Length is < 1 or > 200 || request.Arguments.Length > 200000 || AssistantPrivacy.SecretReason(request.Arguments) is not null) throw new InvalidOperationException("工具请求无效或含敏感凭据。");
        using var document = JsonDocument.Parse(request.Arguments, new() { MaxDepth = 32 }); var args = document.RootElement;
        if (args.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("工具参数必须为对象。");
        if (args.TryGetProperty("environmentId", out var requested) && Guid.Parse(requested.GetString()!) != environmentId) throw new InvalidOperationException("工具目标与用户选择的环境不一致。");
        var environment = GlobalTaskTarget.Find(settings.Current.DesktopSessions, environmentId) ?? throw new InvalidOperationException("目标不存在，禁止回退到主桌面。");
        var category = request.Name switch
        {
            "environments_list" or "tasks_list" => "read",
            "application_launch" or "application_close" or "desktop_step" => "control",
            "task_save" or "task_run" or "task_set_enabled" => "tasks",
            _ => throw new InvalidOperationException("不支持该工具。")
        };
        CheckPermission(category);
        var preview = new AssistantToolPreview(request, environmentId, DesktopTaskPackagePolicy.EnvironmentBinding(environment),
            $"{environment.Name} · {request.Name}", request.Arguments, category, request.Name is "task_save" or "task_set_enabled" or "task_run");
        if (category == "read") return preview;
        if (request.Name is "task_run" or "task_set_enabled")
        {
            var id = Guid.Parse(args.GetProperty("taskId").GetString()!);
            var task = settings.Current.DesktopSessions.Tasks.SingleOrDefault(task => task.Id == id && task.EnvironmentId == environmentId) ?? throw new InvalidOperationException("当前目标不存在该任务。");
            if (request.Name == "task_run" && !task.Enabled) throw new InvalidOperationException("任务已停用，请先确认启用。");
            if (request.Name == "task_set_enabled")
            {
                if (args.GetProperty("expectedRevision").GetInt32() != task.Revision) throw new InvalidOperationException("任务修订已变化。");
                task = task with { Enabled = args.GetProperty("enabled").GetBoolean() };
            }
            return preview with { TaskDefinition = task, ExpectedRevision = task.Revision, Description = DesktopTaskPackagePolicy.Serialize(task) };
        }
        DesktopSessionTask draft;
        if (request.Name == "task_save")
        {
            var id = args.TryGetProperty("taskId", out var taskId) ? Guid.Parse(taskId.GetString()!) : Guid.NewGuid();
            var existing = settings.Current.DesktopSessions.Tasks.SingleOrDefault(task => task.Id == id);
            if (existing is not null && (existing.EnvironmentId != environmentId || !args.TryGetProperty("expectedRevision", out var revision) || revision.GetInt32() != existing.Revision)) throw new InvalidOperationException("任务修订或目标已变化。");
            var steps = args.GetProperty("steps").EnumerateArray().Select(ParseStep).ToArray();
            if (steps.Length is < 1 or > 100) throw new InvalidOperationException("任务需要1至100个步骤。");
            var trigger = args.TryGetProperty("trigger", out var t) ? new SessionTaskTrigger(Enum.Parse<TaskTriggerKind>(t.GetProperty("kind").GetString()!, true),
                t.TryGetProperty("localTime", out var time) ? time.GetString() : null, t.TryGetProperty("timeZoneId", out var zone) ? zone.GetString() : null) : new(TaskTriggerKind.Manual, null, null);
            draft = new(1, id, args.GetProperty("name").GetString()!, environmentId, steps, trigger, existing?.Revision ?? 1,
                args.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean()) { Source = "assistant" };
            if (args.TryGetProperty("scripts", out var scripts)) draft = draft with { Scripts = scripts.EnumerateArray().Select(script => DesktopTaskScript.Create(script.GetProperty("name").GetString()!, script.GetProperty("content").GetString()!)).ToArray() };
            preview = preview with { ExpectedRevision = existing?.Revision };
        }
        else
        {
            SessionTaskStep step;
            if (request.Name == "application_launch")
            {
                var path = args.GetProperty("path").GetString()!;
                var arguments = args.TryGetProperty("arguments", out var a) ? a.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
                var working = args.TryGetProperty("workingDirectory", out var w) ? w.GetString() : null;
                step = new(Guid.NewGuid(), SessionCommandKind.LaunchApplication, LaunchParameters(path, arguments, working), 30, 0);
            }
            else if (request.Name == "application_close") step = new(Guid.NewGuid(), SessionCommandKind.CloseApplication, new Dictionary<string, string> { ["path"] = args.GetProperty("path").GetString()!, ["force"] = "false" }, 30, 0);
            else step = ParseStep(args);
            if (step.Kind == SessionCommandKind.LaunchApplication)
            {
                if (!SessionStartupCommandCodec.TryDecode(step.Parameters, out var entry) || entry is null ||
                    !Path.GetExtension(entry.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) || ScriptHosts.Contains(Path.GetFileNameWithoutExtension(entry.ExecutablePath)))
                    throw new InvalidOperationException("脚本或命令解释器必须保存为完整任务并逐次审核，不能作为普通应用直接启动。");
                preview = preview with { AlwaysConfirm = true };
            }
            if (!DirectKinds.Contains(step.Kind) || step.Kind == SessionCommandKind.ObserveDesktop && step.Parameters.GetValueOrDefault("mode") == "screenshot" || step.Kind == SessionCommandKind.CloseApplication && string.Equals(step.Parameters.GetValueOrDefault("force"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该动作必须保存为任务并单独审核，不能直接执行。");
            draft = new(1, Guid.NewGuid(), "助手单次操作", environmentId, [step], new(TaskTriggerKind.Manual, null, null), 1, true) { Source = "assistant" };
        }
        DesktopTaskPackagePolicy.Validate(draft, environment.Backend);
        DesktopSessionSettingsValidator.Validate(settings.Current.DesktopSessions with { Tasks = settings.Current.DesktopSessions.Tasks.Where(task => task.Id != draft.Id).Append(draft).ToArray() });
        return preview with { TaskDefinition = draft, Description = DesktopTaskPackagePolicy.Serialize(draft) };
    }

    public async Task<AssistantToolResult> ExecuteAsync(AssistantToolPreview preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CheckPermission(preview.Category);
        var environment = GlobalTaskTarget.Find(settings.Current.DesktopSessions, preview.EnvironmentId) ?? throw new InvalidOperationException("目标已移除。");
        if (preview.EnvironmentBinding != DesktopTaskPackagePolicy.EnvironmentBinding(environment)) throw new InvalidOperationException("目标配置已变化，旧授权失效。");
        if (preview.Call.Name == "environments_list") return Result(true, GlobalTaskTarget.All(settings.Current.DesktopSessions).Select(env => new { env.Id, env.Name, backend = env.Id == GlobalTaskTarget.Id ? "Global" : env.Backend.ToString(), ready = isReady?.Invoke(env.Id) == true }));
        if (preview.Call.Name == "tasks_list") return Result(true, settings.Current.DesktopSessions.Tasks.Where(task => task.EnvironmentId == preview.EnvironmentId).Select(task => new { task.Id, task.Name, task.Enabled, task.Revision, task.Trigger }));
        var task = preview.TaskDefinition ?? throw new InvalidOperationException("缺少经过预览的任务。");
        if (preview.Call.Name is "task_save" or "task_set_enabled")
        {
            DesktopSessionTask? saved = null;
            var result = await settings.UpdateAsync(current =>
            {
                var target = GlobalTaskTarget.Find(current.DesktopSessions, preview.EnvironmentId) ?? throw new InvalidOperationException("目标已移除。");
                if (!current.Assistant.Enabled || !current.Assistant.AllowTaskManagement || preview.EnvironmentBinding != DesktopTaskPackagePolicy.EnvironmentBinding(target)) throw new InvalidOperationException("助手任务授权或目标已变化。");
                var old = current.DesktopSessions.Tasks.SingleOrDefault(item => item.Id == task.Id);
                if (old?.Revision != preview.ExpectedRevision || old is not null && old.EnvironmentId != preview.EnvironmentId) throw new InvalidOperationException("任务已变化，请重新预览。");
                saved = task with { Revision = checked((old?.Revision ?? 0) + 1), Source = "assistant", McpApprovalHash = null, McpEnvironmentBinding = DesktopTaskPackagePolicy.EnvironmentBinding(target) };
                saved = saved with { McpApprovalHash = DesktopTaskPackagePolicy.DefinitionHash(saved) };
                DesktopTaskPackagePolicy.Validate(saved, target.Backend);
                return current with { DesktopSessions = current.DesktopSessions with { Tasks = current.DesktopSessions.Tasks.Where(item => item.Id != saved.Id).Append(saved).ToArray() } };
            }, token).ConfigureAwait(false);
            return Result(result.Success, new { saved?.Id, saved?.Enabled, saved?.Revision, error = result.Success ? null : "TaskSaveFailed" });
        }
        if (preview.Call.Name == "task_run" && settings.Current.DesktopSessions.Tasks.SingleOrDefault(item => item.Id == task.Id)?.Revision != preview.ExpectedRevision) throw new InvalidOperationException("任务已变化，请重新预览。");
        var outcome = await runTask(task, Guid.NewGuid(), token).ConfigureAwait(false);
        return Result(outcome.Outcome == DesktopTaskRunOutcome.Succeeded, new { outcome.RunId, outcome.Outcome, outcome.CompletedSteps, outcome.ErrorCode,
            data = outcome.DataJson is { Length: > 32000 } large ? large[..32000] : outcome.DataJson }, outcome.DataJson is not null);
    }

    private void CheckPermission(string category)
    {
        var current = settings.Current.Assistant;
        if (!current.Enabled || !current.SupportsTools || category == "control" && !current.AllowComputerControl || category == "tasks" && !current.AllowTaskManagement)
            throw new InvalidOperationException("助手当前没有此项操作权限。");
    }

    private static SessionTaskStep ParseStep(JsonElement value)
    {
        var kind = Enum.Parse<SessionCommandKind>(value.GetProperty("kind").GetString()!, true);
        var parameters = value.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? "");
        if (kind == SessionCommandKind.LaunchApplication && parameters.TryGetValue("path", out var path))
            parameters = LaunchParameters(path, parameters.TryGetValue("arguments", out var args) ? JsonSerializer.Deserialize<string[]>(args) ?? [] : [], parameters.GetValueOrDefault("workingDirectory")).ToDictionary();
        return new(Guid.NewGuid(), kind, parameters, value.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : 30, 0);
    }

    private static IReadOnlyDictionary<string, string> LaunchParameters(string path, string[] arguments, string? working)
    {
        if (!Path.IsPathFullyQualified(path) || arguments.Length > 128 || arguments.Any(value => value is null || value.Length > 8192) || working is not null && !Path.IsPathFullyQualified(working)) throw new InvalidOperationException("应用路径或参数无效。");
        return SessionStartupCommandCodec.ToParameters(new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), path, arguments, working, 0, 0, true, false));
    }

    private static AssistantToolResult Result(bool success, object value, bool sensitive = false) => new(success, JsonSerializer.Serialize(new { success, result = value }, Json), sensitive);
    private static AssistantToolSchema Schema(string name, string description, string properties, string[]? required = null)
    {
        var props = System.Text.Json.Nodes.JsonNode.Parse(properties)!.AsObject();
        props["environmentId"] = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"string\",\"description\":\"自动模式须填写environments_list返回的目标GUID；手动模式必须与界面目标一致\"}");
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + props.ToJsonString() + ",\"required\":" + JsonSerializer.Serialize(required ?? []) + "}");
        return new(name, description, document.RootElement.Clone());
    }
}
