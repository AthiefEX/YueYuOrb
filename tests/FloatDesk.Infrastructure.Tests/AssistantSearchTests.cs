using System.Runtime.CompilerServices;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantSearchTests
{
    [Fact]
    public async Task SearchRequiresApprovalAndSendsOnlyQueryToSelectedProfileWithoutTools()
    {
        var profile = new AssistantApiProfile(Guid.NewGuid(), "search", "https://search.test/v1", "online-model", true);
        var settings = new AssistantSettings { SearchEnabled = true, SearchProfileId = profile.Id, Profiles = [profile] };
        var model = new Model(); var service = new AssistantSearchService(model, new Credentials());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(settings, "public query", (_, _) => Task.FromResult(false), CancellationToken.None));
        Assert.Null(model.Request);
        var result = await service.SearchAsync(settings, "public query", (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(profile.Endpoint, model.Request!.Settings.Endpoint); Assert.Equal(profile.Model, model.Request.Settings.Model);
        Assert.Null(model.Request.Tools); Assert.False(model.Request.Settings.SupportsTools);
        Assert.Equal("public query", model.Request.Messages[^1].Content); Assert.Equal("profile-key", model.Request.ApiKey);
        Assert.Contains("https://source.test/article", result); Assert.Contains("服务返回", result);
    }
    private sealed class Model : IAssistantModelClient
    {
        public AssistantChatRequest? Request;
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Request = request; await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new("answer") { Sources = [new("article", "https://source.test/article")] }; yield return new(IsComplete: true); }
    }
    private sealed class Credentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult<string?>(purpose.StartsWith("profile:", StringComparison.Ordinal) ? "profile-key" : null);
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) => Task.CompletedTask;
    }
}
