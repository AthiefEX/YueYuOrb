using System.Runtime.CompilerServices;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantLearningTests
{
    [Fact]
    public async Task DeletingSourceCancelsLocalExtractionAndDropsItsQueuedWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Learning-Delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var session = await store.CreateSessionAsync("s", CancellationToken.None);
            var profile = new AssistantApiProfile(Guid.NewGuid(), "local", "http://127.0.0.1:1234/v1", "model", true);
            var source = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "稳定偏好", DateTimeOffset.UtcNow) { ExtractionFingerprint = AssistantLearningWorker.Fingerprint(profile) }; await store.AppendMessageAsync(source, CancellationToken.None);
            var model = new BlockingModel(); using var worker = new AssistantLearningWorker(store, model, new Credentials());
            var processing = worker.ProcessOnceAsync(new() { Enabled = true, Profiles = [profile], Library = new() { ExtractCandidates = true, ExtractionProfileId = profile.Id } }, CancellationToken.None);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await store.DeleteSessionAsync(session.Id, CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing); Assert.Empty(await store.ListCandidatesAsync(CancellationToken.None));
            var summary = await store.LearningSummaryAsync(CancellationToken.None); Assert.Equal(0, summary.Queued + summary.Running + summary.Failed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class BlockingModel : IAssistantModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); yield return new("unreachable"); }
    }

    [Fact]
    public async Task LocalExtractionIsIdempotentAndOnlyCreatesReviewableCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Learning-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var session = await store.CreateSessionAsync("s", CancellationToken.None);
            var source = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "我喜欢深色界面", DateTimeOffset.UtcNow); await store.AppendMessageAsync(source, CancellationToken.None);
            var local = new AssistantApiProfile(Guid.NewGuid(), "local", "http://127.0.0.1:1234/v1", "extract", true);
            var settings = new AssistantSettings { Enabled = true, Profiles = [local], Library = new() { ExtractCandidates = true, ExtractionProfileId = local.Id } };
            using var worker = new AssistantLearningWorker(store, new Model(), new Credentials());
            var hash = AssistantLearningWorker.Fingerprint(local); await store.QueueLearningAsync(source.Id, hash, CancellationToken.None); await store.QueueLearningAsync(source.Id, hash, CancellationToken.None);
            Assert.True(await worker.ProcessOnceAsync(settings, CancellationToken.None)); Assert.False(await worker.ProcessOnceAsync(settings, CancellationToken.None));
            Assert.Empty(await store.ListMemoriesAsync(CancellationToken.None)); var candidate = Assert.Single(await store.ListCandidatesAsync(CancellationToken.None));
            await store.ApproveCandidateAsync(candidate.Id, null, null, CancellationToken.None);
            Assert.Single(await store.ListMemoriesAsync(CancellationToken.None));
            await store.DeleteSessionAsync(session.Id, CancellationToken.None);
            Assert.Empty(await store.RecallAsync("界面", null, true, false, 5000, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Model : IAssistantModelClient
    {
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Assert.True(AssistantEndpointPolicy.IsLocal(new Uri(request.Settings.Endpoint))); Assert.False(request.Settings.SupportsTools); await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new("{\"candidates\":[{\"kind\":\"preference\",\"key\":\"theme\",\"content\":\"用户喜欢深色界面\",\"confidence\":0.9,\"evidence\":\"我喜欢深色界面\"}]}"); yield return new(IsComplete: true); }
    }
    private sealed class Credentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult<string?>(null);
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) => Task.CompletedTask;
    }
}
