using System.Net;
using System.Text;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantApiClientTests
{
    [Fact]
    public async Task ProviderCitationMetadataBecomesSafeSourceLinks()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"citations\":[\"https://source.test/one\",\"https://user:pass@evil.test/\"],\"choices\":[{\"message\":{\"content\":\"answer\",\"annotations\":[{\"url_citation\":{\"title\":\"two\",\"url\":\"https://source.test/two\"}}]},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") })));
        var sources = new List<AssistantSource>();
        await foreach (var delta in new AssistantApiClient(http).StreamAsync(new(new() { Endpoint = "https://example.test/v1", Model = "test", Stream = false }, [], null), CancellationToken.None)) sources.AddRange(delta.Sources);
        Assert.Equal(2, sources.Count); Assert.DoesNotContain(sources, source => source.Url.Contains("evil", StringComparison.Ordinal)); Assert.Contains(sources, source => source.Title == "two");
    }

    [Fact]
    public async Task MultilineSseAndChineseUtf8SurviveSingleByteNetworkReads()
    {
        const string events = "data: {\"choices\":[{\"delta\":\n" + "data: {\"content\":\"逐字节中文🙂\"}}]}\r\n\r\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new SingleByteStream(Encoding.UTF8.GetBytes(events))) })));
        var deltas = new List<AssistantChatDelta>();
        await foreach (var delta in new AssistantApiClient(http).StreamAsync(new(new() { Endpoint = "https://example.test/v1", Model = "test" }, [], null), CancellationToken.None)) deltas.Add(delta);
        Assert.Equal("逐字节中文🙂", string.Concat(deltas.Select(delta => delta.Text))); Assert.True(deltas[^1].IsComplete);
    }

    [Fact]
    public async Task CancellationAndOversizedOutputCannotCompleteSuccessfully()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 2000) + "\"}}]}\n\ndata: [DONE]\n\n") })));
        var request = new AssistantChatRequest(new() { Endpoint = "https://example.test/v1", Model = "test", MaxResponseCharacters = 1000 }, [], null);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in new AssistantApiClient(http).StreamAsync(request, CancellationToken.None)) { } });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in new AssistantApiClient(http).StreamAsync(request, canceled.Token)) { } });
    }

    private sealed class SingleByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task StreamCombinesUtf8TextAndToolFragmentsWithoutPuttingKeyInPayload()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"environments_list\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var client = new AssistantApiClient(http);
        var request = new AssistantChatRequest(new() { Enabled = true, Endpoint = "https://example.test/v1", Model = "test" },
            [new(Guid.NewGuid(), Guid.NewGuid(), "user", "hello", DateTimeOffset.UtcNow)], "test-secret");
        var deltas = new List<AssistantChatDelta>();
        await foreach (var delta in client.StreamAsync(request, CancellationToken.None)) deltas.Add(delta);
        Assert.Equal("你好", string.Concat(deltas.Select(delta => delta.Text)));
        Assert.Equal("environments_list", Assert.Single(deltas, delta => delta.ToolCall is not null).ToolCall!.Name);
        Assert.True(deltas[^1].IsComplete);
        Assert.DoesNotContain("test-secret", body!);
    }

    [Fact]
    public async Task HttpErrorDoesNotExposeRemoteErrorBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("password=private-value") })));
        var client = new AssistantApiClient(http);
        var request = new AssistantChatRequest(new() { Endpoint = "https://example.test/v1", Model = "test" }, [], null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in client.StreamAsync(request, CancellationToken.None)) { } });
        Assert.Contains("401", error.Message); Assert.DoesNotContain("private-value", error.Message);
    }

    [Fact]
    public async Task MissingCompletionMarkerIsNotReportedAsSuccessfulCompletion()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", Encoding.UTF8, "text/event-stream") })));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var _ in new AssistantApiClient(http).StreamAsync(new(new() { Endpoint = "https://example.test/v1", Model = "test" }, [], null), CancellationToken.None)) { } });
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
}
