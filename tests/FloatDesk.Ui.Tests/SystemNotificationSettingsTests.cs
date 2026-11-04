using System.Reflection;
using System.Text.Json;
using FloatDesk.App;
using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class SystemNotificationSettingsTests
{
    [Fact]
    public void NotificationSwitchMergesWithoutOverwritingOtherGeneralSettings()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        Assert.True(vm.ShowSystemNotifications);
        vm.ShowSystemNotifications = false;
        var latest = initial with { General = initial.General with { Theme = AppTheme.Dark, StartWithWindows = true } };
        var candidate = vm.BuildCandidate(latest);
        Assert.Equal(AppTheme.Dark, candidate.General.Theme);
        Assert.True(candidate.General.StartWithWindows);
        Assert.False(ReadFlag(candidate));
        Assert.False(ReadFlag(SettingsViewModel.From(initial).BuildCandidate(candidate)));
        Assert.False(SettingsViewModel.From(candidate).ShowSystemNotifications);
    }

    [Fact]
    public void TrayChecksLatestPreferenceWhenQueuedNotificationIsDelivered()
    {
        RunSta(() =>
        {
            var context = new QueuedContext();
            var original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var enabled = true;
                var evaluated = new List<bool>();
                var constructor = typeof(TrayService).GetConstructors().Single();
                var parameters = constructor.GetParameters();
                Assert.Contains(parameters, p => p.Name == "notificationsEnabled");
                var arguments = parameters.Select(p => p.ParameterType == typeof(Action)
                    ? (object)(Action)(() => { })
                    : p.Name == "notificationsEnabled" ? (Func<bool>)(() => { evaluated.Add(enabled); return enabled; }) : null).ToArray();
                using var tray = (TrayService)constructor.Invoke(arguments);
                var icon = (System.Windows.Forms.NotifyIcon)typeof(TrayService)
                    .GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                icon.Visible = false; // Exercise the real presenter without showing test notifications.
                var worker = new Thread(() => tray.ShowWarning("queued", "message"));
                worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
                enabled = false;
                context.Drain();
                Assert.False(Assert.Single(evaluated));
                tray.ShowWarning("disabled", "message");
                Assert.Equal(2, evaluated.Count);
                Assert.False(evaluated[1]);
                enabled = true;
                tray.ShowWarning("enabled", "message");
                Assert.Equal(3, evaluated.Count);
                Assert.True(evaluated[2]);
                tray.Dispose();
                tray.ShowWarning("closed", "message");
                Assert.Equal(3, evaluated.Count);
            }
            finally { SynchronizationContext.SetSynchronizationContext(original); }
        });
    }

    [Fact]
    public void DeliveryPolicyBlocksAndRestoresTheActualPresentationAction()
    {
        var enabled = false;
        var presentations = 0;
        SystemNotificationPolicy.Deliver(() => enabled, () => presentations++);
        Assert.Equal(0, presentations);
        enabled = true;
        SystemNotificationPolicy.Deliver(() => enabled, () => presentations++);
        Assert.Equal(1, presentations);
        enabled = false;
        SystemNotificationPolicy.Deliver(() => enabled, () => presentations++);
        Assert.Equal(1, presentations);
    }

    private static bool ReadFlag(AppSettings settings)
    {
        var general = JsonSerializer.SerializeToElement(settings.General);
        Assert.True(general.TryGetProperty("ShowSystemNotifications", out var flag));
        return flag.GetBoolean();
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue(() => callback(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }
}
