using FloatDesk.App.Brightness;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class NightDimmingPreferenceTests
{
    [Fact]
    public async Task FailedCommitDoesNotOverrideLaterSettingsSave()
    {
        var settings = new MemorySettings();
        var delayed = new DelayedSettings(settings) { RejectNext = true };
        delayed.Release.SetResult();
        var preference = new NightDimmingPreference(delayed);
        preference.Preview(80);
        Assert.False((await preference.Commit(80)).Success);
        await settings.UpdateAsync(current => current with
        { Brightness = current.Brightness with { NightDimmingPercent = 60 } }, CancellationToken.None);
        Assert.Equal(60, preference.Resolve());
    }
    [Fact]
    public async Task SlowOlderSaveDoesNotReplaceNewerPreviewAndFinalValueSurvivesReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYuOrb-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FloatDesk.Infrastructure.Settings.JsonSettingsStore(directory);
            using var settings = new FloatDesk.Infrastructure.Settings.SettingsCoordinator(store);
            await settings.InitializeAsync(CancellationToken.None);
            var delayed = new DelayedSettings(settings);
            var preference = new NightDimmingPreference(delayed);
            preference.Preview(50);
            var first = preference.Commit(50);
            await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preference.Preview(80);
            delayed.Release.SetResult();
            Assert.True((await first).Success);
            Assert.Equal(80, preference.Resolve());
            var second = preference.Commit(80);
            await Task.Run(() => Assert.True(second.Wait(TimeSpan.FromSeconds(5))));
            Assert.True((await second).Success);
            Assert.Equal(80, preference.Resolve());
            using var reloaded = new FloatDesk.Infrastructure.Settings.JsonSettingsStore(directory);
            Assert.Equal(80, (await reloaded.LoadAsync(CancellationToken.None)).Brightness.NightDimmingPercent);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DelayedSettings(ISettingsCoordinator inner) : ISettingsCoordinator
    {
        public bool RejectNext { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppSettings Current => inner.Current;
        public event Action<AppSettings>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public async Task<OperationResult> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task;
            if (RejectNext) { RejectNext = false; return OperationResult.Fail("TestFailure", "test"); }
            return await inner.UpdateAsync(update, token);
        }
    }
    private sealed class MemorySettings : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault();
        public event Action<AppSettings>? Changed;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken token)
        { Current = update(Current); Changed?.Invoke(Current); return Task.FromResult(OperationResult.Ok()); }
    }
}
