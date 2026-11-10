using System.Net;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantEmbeddingTests
{
    [Fact]
    public async Task HybridRecallUsesCurrentSpaceAndFiltersProjectBeforeReturningSemanticHits()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Semantic-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var a = await store.CreateProjectAsync("A", CancellationToken.None); var b = await store.CreateProjectAsync("B", CancellationToken.None);
            foreach (var project in new[] { a, b })
            {
                var document = new AssistantDocument(Guid.NewGuid(), project.Id, project.Name + ".txt", "local", new string(project.Name[0], 64), true, "deny", DateTimeOffset.UtcNow);
                await store.SaveDocumentAsync(document, [new(Guid.NewGuid(), document.Id, 0, "段1", project.Id == a.Id ? "深蓝色方案" : "B的私密蓝色资料")], CancellationToken.None);
            }
            using var http = new HttpClient(new SemanticHandler());
            var profile = new AssistantApiProfile(Guid.NewGuid(), "local", "http://127.0.0.1:1234/v1", "embed-v1", true);
            var settings = new AssistantSettings { UseMemory = true, Profiles = [profile], Library = new() { UseKnowledge = true, EmbeddingEnabled = true, EmbeddingProfileId = profile.Id } };
            var retrieval = new AssistantRetrievalService(store, new AssistantEmbeddingClient(http), new NoCredentials());
            Assert.Equal(2, await retrieval.RebuildAsync(settings, null, CancellationToken.None));
            var hits = await retrieval.RecallAsync(settings, "海洋", a.Id, 5000, CancellationToken.None);
            Assert.Equal("深蓝色方案", Assert.Single(hits).Content);
            var changed = settings with { Profiles = [profile with { Model = "embed-v2" }] };
            Assert.Empty(await retrieval.RecallAsync(changed, "海洋", a.Id, 5000, CancellationToken.None));
            await store.DeleteProjectAsync(a.Id, CancellationToken.None); await store.DeleteProjectAsync(b.Id, CancellationToken.None);
            Assert.Empty(await store.ReadVectorsAsync(AssistantEmbeddingClient.Fingerprint(profile), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SemanticHandler : HttpMessageHandler
    {
        private static readonly float[] Values = [1f, 0f];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var data = json.RootElement.GetProperty("input").EnumerateArray().Select((_, index) => new { index, embedding = Values }).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { data })) };
        }
    }
    private sealed class NoCredentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult<string?>(null);
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task EmbeddingsAreLocalBoundedAndModelSpacesDoNotMix()
    {
        using var http = new HttpClient(new Handler()); var client = new AssistantEmbeddingClient(http);
        var local = new AssistantApiProfile(Guid.NewGuid(), "local", "http://127.0.0.1:1234/v1", "embed-v1", true);
        var vectors = await client.EmbedAsync(local, ["主题颜色"], null, CancellationToken.None);
        Assert.Equal(3, Assert.Single(vectors).Length);
        Assert.NotEqual(AssistantEmbeddingClient.Fingerprint(local), AssistantEmbeddingClient.Fingerprint(local with { Model = "embed-v2" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(local with { Endpoint = "https://cloud.test/v1" }, ["private"], null, CancellationToken.None));
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Assert.EndsWith("/v1/embeddings", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"index\":0,\"embedding\":[1,0,0]}]}") }); }
    }
}
