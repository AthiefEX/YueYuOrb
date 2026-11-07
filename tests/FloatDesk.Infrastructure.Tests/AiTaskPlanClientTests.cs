using System.Net;
using System.Net.Http.Json;
using FloatDesk.Infrastructure.DesktopSessions;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AiTaskPlanClientTests
{
    [Fact]
    public async Task GeneratedPlanIsValidatedAndBoundToEnvironment()
    {
        var id = Guid.NewGuid();
        using var http = new HttpClient(new Handler(id));
        var task = await new AiTaskPlanClient(http, new Uri("https://example.test/v1/chat/completions"), "test")
            .GenerateDraftAsync("start approved app", id, null, default);
        Assert.Equal(id, task.EnvironmentId);
    }

    private sealed class Handler(Guid environmentId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var task = FloatDesk.Core.Models.DesktopAutomation.TaskDocumentV2.Create(environmentId, []);
            var response = new { choices = new[] { new { message = new { content = System.Text.Json.JsonSerializer.Serialize(task) } } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }
}
