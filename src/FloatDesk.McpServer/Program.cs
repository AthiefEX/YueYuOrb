using System.Text.Json;
using FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.McpServer;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly FloatDesk.Infrastructure.DesktopSessions.DesktopAutomationTaskCatalog Catalog =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatDesk", "desktop-sessions"));

    private static async Task Main()
    {
        await foreach (var request in ReadRequestsAsync())
        {
            var response = Handle(request);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            await Console.Out.FlushAsync();
        }
    }

    private static async IAsyncEnumerable<JsonElement> ReadRequestsAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement? parsed = null;
            try { using var document = JsonDocument.Parse(line); parsed = document.RootElement.Clone(); }
            catch (JsonException) { }
            if (parsed is JsonElement request) yield return request;
        }
    }

    private static object Handle(JsonElement request)
    {
        if (!request.TryGetProperty("method", out var methodElement)) return Error(null, -32600, "Invalid request");
        var method = methodElement.GetString();
        return method switch
        {
            "initialize" => new { jsonrpc = "2.0", id = Id(request), result = new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "YueYuOrb.McpServer", version = "1" } } },
            "notifications/initialized" => new { jsonrpc = "2.0", result = new { } },
            "tools/list" => Tools(request),
            "tools/call" => Call(request),
            _ => Error(Id(request), -32601, "Method not supported")
        };
    }

    private static object Tools(JsonElement request) => new { jsonrpc = "2.0", id = Id(request), result = new { tools = new[] { Tool("environments_list", "List configured desktop environments."), Tool("tasks_schema", "Describe the safe task DSL."), Tool("tasks_validate", "Validate a task draft without executing it."), Tool("tasks_create_draft", "Create a task draft; this never executes it."), Tool("tasks_list", "List persisted task drafts."), Tool("tasks_get", "Read one persisted task draft."), Tool("tasks_submit", "Submit a task only with an existing scoped grant.") } } };

    private static object Call(JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        var name = request.GetProperty("params").GetProperty("name").GetString();
        object result = name switch
        {
            "environments_list" => new { environments = Array.Empty<object>() },
            "tasks_schema" => new { format = "TaskDocumentV2", execution = "draft-only-until-submit", operations = Enum.GetNames<GuestOperationKind>() },
            "tasks_validate" => Validate(arguments),
            "tasks_create_draft" => CreateDraft(arguments),
            "tasks_list" => ListTasks().GetAwaiter().GetResult(),
            "tasks_get" => GetTask(arguments).GetAwaiter().GetResult(),
            "tasks_submit" => new { status = "needs_authorization", executionStarted = false, errorCode = "NeedsAuthorization", message = "A locally approved TaskGrant and idempotency key are required." },
            _ => new { error = "ToolNotFound" }
        };
        return new { jsonrpc = "2.0", id = Id(request), result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } } } };
    }

    private static object CreateDraft(JsonElement arguments)
    {
        try
        {
            var task = arguments.Deserialize<TaskDocumentV2>(JsonOptions);
            if (task is null) return new { status = "invalid", errorCode = "TaskMissing" };
            var validation = TaskDocumentV2Validator.Validate(task);
            if (!validation.IsValid) return new { status = "invalid", errors = validation.Errors };
            var saved = Catalog.CreateAsync(task, CancellationToken.None).GetAwaiter().GetResult();
            return new { status = "draft", taskId = saved.Id, revision = saved.Revision, executionStarted = false };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return new { status = "invalid", errorCode = exception.Message }; }
    }

    private static async Task<object> ListTasks() => new { tasks = await Catalog.ListAsync(CancellationToken.None).ConfigureAwait(false) };

    private static async Task<object> GetTask(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("taskId", out var id) || !Guid.TryParse(id.GetString(), out var taskId))
            return new { errorCode = "TaskIdInvalid" };
        var task = (await Catalog.ListAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(item => item.Id == taskId);
        return task is null ? new { errorCode = "TaskNotFound" } : new { task };
    }

    private static object Validate(JsonElement arguments)
    {
        try
        {
            var task = arguments.Deserialize<TaskDocumentV2>(JsonOptions);
            if (task is null) return new { valid = false, error = "TaskMissing" };
            var validation = TaskDocumentV2Validator.Validate(task);
            return new { valid = validation.IsValid, errors = validation.Errors };
        }
        catch (JsonException) { return new { valid = false, error = "TaskJsonInvalid" }; }
    }

    private static object Tool(string name, string description) => new { name, description, inputSchema = new { type = "object" } };
    private static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static JsonElement? Id(JsonElement request) => request.TryGetProperty("id", out var id) ? id.Clone() : null;
}
