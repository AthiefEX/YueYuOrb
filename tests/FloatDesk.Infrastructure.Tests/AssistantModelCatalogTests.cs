using System.Net;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantModelCatalogTests
{
    [Fact]
    public async Task ProbeReturnsActualModelReplyInsteadOfOnlyASuccessFlag()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Actual probe reply\"},\"finish_reason\":\"stop\"}]}") }));
        var reply = await new AssistantModelCatalog(http).TestAsync(new() { Endpoint = "https://example.test/v1", Model = "test", Stream = false }, null, CancellationToken.None);
        Assert.Equal("Actual probe reply", reply);
    }

    private static readonly string[] ExpectedModels = ["model-a", "model-b"];
    [Fact]
    public async Task DiscoveryUsesTheChosenKeyAndFiltersDuplicateModels()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https://example.test/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("selected-key", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"model-b\"},{\"id\":\"model-a\"},{\"id\":\"model-a\"},{\"id\":\"bad\\nmodel\"}]}") };
        }));
        Assert.Equal(ExpectedModels, await new AssistantModelCatalog(http).ListAsync("https://example.test/v1/chat/completions", "selected-key", false, CancellationToken.None));
    }

    [Fact]
    public async Task ListingDoesNotClaimModelWorksAndActualProbeSendsSelectedModel()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("chosen-model", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.Forbidden) { Content = new StringContent("private-provider-detail") };
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AssistantModelCatalog(http).TestAsync(new() { Endpoint = "https://example.test/v1", Model = "chosen-model" }, "key", CancellationToken.None));
        Assert.Equal(1, calls); Assert.Contains("403", error.Message); Assert.DoesNotContain("private-provider-detail", error.Message);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
