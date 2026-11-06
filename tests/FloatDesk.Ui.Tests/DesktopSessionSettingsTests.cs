using FloatDesk.App.DesktopSessions;
using FloatDesk.App.ViewModels;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;
using System.Reflection;
using System.Windows;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class DesktopSessionSettingsTests
{
    [Fact]
    public void SettingsMergePreservesOtherPages()
    {
        var original = AppSettings.CreateDefault();
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "隔离桌面",
            DesktopSessionBackendKind.LocalChild,
            "local",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);
        var concurrent = original with
        {
            BrowserTopmost = true,
            DesktopSessions = new DesktopSessionSettings(
                Enabled: true,
                Environments: [environment],
                Tasks: [])
        };
        var viewModel = SettingsViewModel.From(original);

        var untouched = viewModel.BuildCandidate(concurrent);
        Assert.Same(
            concurrent.DesktopSessions,
            untouched.DesktopSessions);
        Assert.True(untouched.BrowserTopmost);

        viewModel.ShowLauncherDock = false;
        viewModel.ReplaceDesktopSessionSettings(
            concurrent.DesktopSessions with { Enabled = false });
        var edited = viewModel.BuildCandidate(concurrent);

        Assert.False(edited.DesktopSessions.Enabled);
        Assert.Equal(environment.Id, Assert.Single(
            edited.DesktopSessions.Environments).Id);
        Assert.False(edited.ShowLauncherDock);
        Assert.True(edited.BrowserTopmost);
    }

    [Fact]
    public void OldWheelItemsRemainValid()
    {
        Assert.Equal(0, (int)WheelActionKind.RegionCapture);
        Assert.Equal(10, (int)WheelActionKind.ExtractRegionText);
        Assert.Equal(11, (int)WheelActionKind.DesktopSessions);
        Assert.Equal(12, (int)WheelActionKind.RunDesktopTask);

        var defaults = AppSettings.CreateDefault();
        Assert.Contains(
            WheelActionKind.ExtractRegionText,
            defaults.WheelActions);
        Assert.DoesNotContain(
            WheelActionKind.DesktopSessions,
            defaults.WheelActions);
    }

    [Fact]
    public void SessionViewNotDismissedOnDeactivate()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var window = new DesktopSessionWindow(
                    new FakeRdpControl(),
                    "测试环境");
                window.Show();
                typeof(Window)
                    .GetMethod(
                        "OnDeactivated",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                Assert.True(window.IsVisible);
                window.AllowClose();
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(5)),
            "桌面分身窗口测试超时。");
        Assert.Null(failure);
    }

    private sealed class FakeRdpControl : IRdpControl
    {
        public Task ConfigureAsync(
            int width,
            int height,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }
}
