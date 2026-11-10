using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatDesk.Core.Models;

public sealed record DesktopTaskScript(string Name, string Content, string Sha256)
{
    public static DesktopTaskScript Create(string name, string content) => new(name, content,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
}

public static class DesktopTaskPackagePolicy
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(DesktopSessionTask task) => JsonSerializer.Serialize(task, JsonOptions);
    public static DesktopSessionTask Parse(string json)
    {
        if (json.Length > 524288) throw new InvalidOperationException("TaskTooLarge");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("TaskMissing");
        if (document.RootElement.EnumerateObject().Any(property => property.Name.Equals("operations", StringComparison.OrdinalIgnoreCase)))
        {
            var old = JsonSerializer.Deserialize<DesktopAutomation.TaskDocumentV2>(json, JsonOptions)
                ?? throw new InvalidOperationException("TaskMissing");
            if (old.Mode != DesktopAutomation.TaskExecutionMode.Fixed || old.ShutdownAfterSuccess || old.CredentialRef is not null || old.Operations is null)
                throw new InvalidOperationException("LegacyTaskRequiresManualConversion");
            var steps = old.Operations.Select(operation => new SessionTaskStep(Guid.NewGuid(), operation.Kind switch
            {
                DesktopAutomation.GuestOperationKind.StartApplication => SessionCommandKind.LaunchApplication,
                DesktopAutomation.GuestOperationKind.StopApplication => SessionCommandKind.CloseApplication,
                _ => throw new InvalidOperationException("LegacyOperationRequiresManualConversion")
            }, LegacyParameters(operation), Math.Clamp(old.MaxDurationSeconds, 1, 600), 0)).ToArray();
            return AsExternalDraft(new(1, old.Id, "导入旧版任务", old.EnvironmentId, steps,
                new(TaskTriggerKind.Manual, null, null), Math.Max(1, old.Revision), false), "legacy-import");
        }
        return JsonSerializer.Deserialize<DesktopSessionTask>(json, JsonOptions) ?? throw new InvalidOperationException("TaskMissing");
    }

    private static IReadOnlyDictionary<string,string> LegacyParameters(DesktopAutomation.GuestOperation operation)
    {
        if (operation.Parameters is null || operation.Parameters.Count != 1 || !operation.Parameters.TryGetValue("path", out var path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("LegacyParametersRequireManualConversion");
        return operation.Kind == DesktopAutomation.GuestOperationKind.StartApplication
            ? SessionStartupCommandCodec.ToParameters(new(Guid.NewGuid(), "导入应用", path, [], null, 0, 0, true, false))
            : operation.Parameters;
    }

    public static DesktopSessionTask AsExternalDraft(DesktopSessionTask task, string source) => task with
    {
        Enabled = false, McpApprovalHash = null, McpEnvironmentBinding = null, Source = source,
        Trigger = new(TaskTriggerKind.Manual, null, null)
    };

    public static string DefinitionHash(DesktopSessionTask task)
    {
        var canonical = task with { McpApprovalHash = null, Steps = task.Steps.Select(step => step with
        { Parameters = step.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value) }).ToArray() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(canonical)))).ToLowerInvariant();
    }
    public static bool IsApproved(DesktopSessionTask task) => task.McpApprovalHash is { } hash && hash == DefinitionHash(task);
    public static string EnvironmentBinding(EnvironmentProfile environment) => environment.Backend + ":" + environment.BackendProfileId;

    public static void Validate(DesktopSessionTask task, DesktopSessionBackendKind backend)
    {
        if (task.FormatVersion != 1 || task.Id == Guid.Empty || task.EnvironmentId == Guid.Empty || task.Revision < 1 ||
            string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 80 || task.Steps is null || task.Steps.Count > 200 ||
            task.Rules is null || task.Rules.Length > 32768 || task.Scripts is null || task.Scripts.Count > 8 ||
            task.Trigger is null || !Enum.IsDefined(task.Trigger.Kind)) throw new InvalidOperationException("TaskDefinitionInvalid");
        if (task.Steps.Any(step => step is null) || task.Scripts.Any(script => script is null))
            throw new InvalidOperationException("TaskDefinitionInvalid");
        if (task.Steps.Select(step => step.Id).Distinct().Count() != task.Steps.Count) throw new InvalidOperationException("DuplicateStepId");
        foreach (var script in task.Scripts)
        {
            if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 80 || script.Name.IndexOfAny(['\\','/',':','*','?','\"','<','>','|']) >= 0 ||
                !script.Name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || script.Name.Any(char.IsControl) ||
                script.Content is null || script.Content.Length > 65536 ||
                !string.Equals(DesktopTaskScript.Create(script.Name, script.Content).Sha256, script.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ScriptAssetInvalid");
        }
        if (task.Scripts.Select(script => script.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != task.Scripts.Count)
            throw new InvalidOperationException("DuplicateScriptName");
        foreach (var step in task.Steps)
        {
            if (step.Id == Guid.Empty || !Enum.IsDefined(step.Kind) || step.Parameters is null || step.TimeoutSeconds is < 1 or > 600 ||
                step.RetryCount is < 0 or > 3) throw new InvalidOperationException("StepInvalid");
            if (step.Kind == SessionCommandKind.RunSystemScript && backend != DesktopSessionBackendKind.HyperV)
                throw new InvalidOperationException("GuestAdminRequiresHyperV");
            if(step.Kind==SessionCommandKind.SetBrightness&&task.EnvironmentId!=GlobalTaskTarget.Id)
                throw new InvalidOperationException("BrightnessRequiresGlobalTarget");
            if(step.Kind==SessionCommandKind.SetBrightness&&(!step.Parameters.TryGetValue("percent",out var percentText)||
                !int.TryParse(percentText,out var percent)||percent is <0 or >100))throw new InvalidOperationException("BrightnessPercentInvalid");
            if (step.Parameters.TryGetValue("scriptName", out var name) && !string.IsNullOrWhiteSpace(name) && !task.Scripts.Any(script => script.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("ScriptAssetMissing");
            if (step.Kind == SessionCommandKind.RunSystemScript && (!step.Parameters.TryGetValue("scriptName", out var required) || string.IsNullOrWhiteSpace(required))) throw new InvalidOperationException("SystemScriptAssetRequired");
            if (step.Kind is SessionCommandKind.RunScript or SessionCommandKind.RunSystemScript &&
                step.Parameters.TryGetValue("arguments", out var arguments))
            {
                try
                {
                    var items = JsonSerializer.Deserialize<string[]>(arguments);
                    if (items is null || items.Length > 128 || items.Any(item => item is null || item.Length > 8192))
                        throw new InvalidOperationException("ScriptArgumentsInvalid");
                }
                catch (JsonException) { throw new InvalidOperationException("ScriptArgumentsInvalid"); }
            }
        }
        if (Serialize(task).Length > 524288) throw new InvalidOperationException("TaskTooLarge");
    }

    public static IReadOnlyDictionary<string,string> ResolveParameters(DesktopSessionTask task, SessionTaskStep step)
    {
        if (!step.Parameters.TryGetValue("scriptName", out var name) || string.IsNullOrWhiteSpace(name)) return step.Parameters;
        var script = task.Scripts.Single(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var result = step.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
        result["scriptContent"] = script.Content; result["scriptSha256"] = script.Sha256;
        return result;
    }
}
