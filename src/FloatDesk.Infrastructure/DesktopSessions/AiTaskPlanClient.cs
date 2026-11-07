using System.Net.Http.Json;
using System.Text.Json;
using FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.Infrastructure.DesktopSessions;

public sealed class AiTaskPlanClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;

    public AiTaskPlanClient(HttpClient http, Uri endpoint, string model)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http")) throw new ArgumentException("A valid AI endpoint is required.", nameof(endpoint));
        _endpoint = endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("Model is required.", nameof(model)) : model.Trim();
    }

    public async Task<TaskDocumentV2> GenerateDraftAsync(
        string instruction,
        Guid environmentId,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("Instruction is required.", nameof(instruction));
        var request = new { model = _model, temperature = 0, response_format = new { type = "json_object" }, messages = new[] { new { role = "system", content = "Return only a TaskDocumentV2 JSON object. Never return shell commands, credentials, or permissions." }, new { role = "user", content = instruction } } };
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(request) };
        if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("AiTaskPlanEmpty");
        var task = JsonSerializer.Deserialize<TaskDocumentV2>(content, JsonOptions) ?? throw new InvalidOperationException("AiTaskPlanInvalid");
        if (task.EnvironmentId != environmentId) throw new InvalidOperationException("AiTaskPlanEnvironmentMismatch");
        var validation = TaskDocumentV2Validator.Validate(task);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0].Code);
        return task;
    }
}
