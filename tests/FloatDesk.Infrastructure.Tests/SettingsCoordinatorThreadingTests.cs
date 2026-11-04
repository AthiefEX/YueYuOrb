using System.Collections.Concurrent;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class SettingsCoordinatorThreadingTests
{
    [Fact]
    public async Task OutstandingSaveReleasesGateWithoutPumpingCallerContext()
    {
        var store = new DelayedStore();
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync(CancellationToken.None);
        var context = new QueuedContext();
        Task<OperationResult> first;
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            first = settings.UpdateAsync(current => current, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        var second = Task.Run(() => settings.UpdateAsync(current => current, CancellationToken.None));
        store.Release.SetResult();
        var completed = await Task.WhenAny(Task.WhenAll(first, second), Task.Delay(1000));
        var dependedOnContext = !first.IsCompleted || !second.IsCompleted;
        // Release a captured continuation even on the RED run so no task is abandoned.
        context.Drain();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dependedOnContext, "Saving must finish without the caller's UI context.");
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue(() => callback(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }
    private sealed class DelayedStore : ISettingsStore
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken token) => Task.FromResult(AppSettings.CreateDefault());
        public Task SaveAsync(AppSettings value, CancellationToken token) => Release.Task;
    }
}
