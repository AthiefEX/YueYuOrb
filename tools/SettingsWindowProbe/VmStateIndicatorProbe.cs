using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;
using FloatDesk.Windows.DesktopSessions;

namespace SettingsWindowProbe;

internal static partial class Program
{
    /// <summary>
    /// Reproduces the reported defect end to end: a running machine is shown with a green dot,
    /// the machine is then stopped outside the settings window (exactly what closing the console
    /// with the shutdown close policy does), and the dot must follow within one poll interval.
    /// </summary>
    private static async Task VerifyVmStateIndicatorAsync()
    {
        var manager = new HyperVVirtualMachineManager();
        var machines = await manager.ListAsync(CancellationToken.None);
        if (machines.Count == 0) throw new InvalidOperationException("No Hyper-V machine to probe.");
        var machine = machines.First(vm => vm.State is HyperVVirtualMachineState.Off or HyperVVirtualMachineState.Running);
        Console.WriteLine($"vm.indicator.target={machine.Name};initial={machine.State}");
        if (machine.State == HyperVVirtualMachineState.Running)
            await StopExternallyAsync(machine.Name);

        var paths = new AppDataPaths();
        using var store = new JsonSettingsStore(paths.Root);
        var settings = await store.LoadAsync(CancellationToken.None);
        var window = new SettingsWindow(
            new FixedSettingsCoordinator(settings), paths, null,
            new NullCredentialStore(), new NullTranslationProviderFactory(), () => []);
        try
        {
            window.Show();
            window.UpdateLayout();
            window.SelectDesktopSessionsPage();
            window.UpdateLayout();
            await PumpUntilRefreshedAsync(window);
            Console.WriteLine("vm.indicator.stopped=" + Describe(window, machine.Id));

            await manager.StartAsync(machine.Id, CancellationToken.None);
            await PumpPowerChangeAsync(window, machine.Id, "running");
            Console.WriteLine("vm.indicator.running=" + Describe(window, machine.Id));

            // The console close policy calls this same script; running it here keeps the probe
            // independent from the console window while still producing the same host state.
            await manager.ApplyCloseActionAsync(machine.Id, HyperVConsoleCloseAction.Shutdown, CancellationToken.None);
            await PumpPowerChangeAsync(window, machine.Id, "off");
            var afterClose = Describe(window, machine.Id);
            Console.WriteLine("vm.indicator.afterClose=" + afterClose);
            if (!afterClose.Contains("已关闭", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The environment dot still reports a running machine after it was closed: " + afterClose);
            Console.WriteLine("vm.indicator.followsClose=PASS");
        }
        finally
        {
            window.Close();
            await StopExternallyAsync(machine.Name);
        }
    }

    private static async Task PumpUntilRefreshedAsync(SettingsWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var running = typeof(SettingsWindow).GetField("_vmRefreshRunning", flags)!;
        var states = typeof(SettingsWindow).GetField("_desktopVmStates", flags)!;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            var busy = (bool)running.GetValue(window)!;
            var snapshot = (System.Collections.IDictionary)states.GetValue(window)!;
            if (!busy && snapshot.Count > 0) { await PumpAsync(400); return; }
            await PumpAsync(120);
        }
        throw new InvalidOperationException("The settings page never finished its first virtual-machine refresh.");
    }

    private static async Task PumpPowerChangeAsync(SettingsWindow window, Guid vmId, string expected)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var states = (System.Collections.IDictionary)typeof(SettingsWindow).GetField("_desktopVmStates", flags)!.GetValue(window)!;
        var until = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < until)
        {
            if (states[vmId] is HyperVVirtualMachineState state &&
                state.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                await PumpAsync(300);
                return;
            }
            await PumpAsync(200);
        }
        Console.WriteLine($"vm.indicator.timeout(expected={expected})=" + Describe(window, vmId));
    }

    private static string Describe(SettingsWindow window, Guid vmId)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var states = (System.Collections.IDictionary)typeof(SettingsWindow).GetField("_desktopVmStates", flags)!.GetValue(window)!;
        var cached = states[vmId]?.ToString() ?? "<none>";
        var list = (System.Windows.Controls.ListBox?)typeof(SettingsWindow).GetField("_desktopEnvironmentList", flags)!.GetValue(window);
        var dots = new List<string>();
        if (list is not null)
        {
            void Visit(DependencyObject element)
            {
                if (element is Ellipse dot)
                {
                    var profile = dot.DataContext as FloatDesk.Core.Models.EnvironmentProfile;
                    var color = (dot.Fill as SolidColorBrush)?.Color.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
                    var tip = dot.ToolTip?.ToString() ?? "<none>";
                    dots.Add($"name='{dot.Name}' {profile?.Name ?? "?"}[{profile?.BackendProfileId}]={tip}/{color}");
                }
                if (element is ContentPresenter presenter && presenter.Content is FloatDesk.Core.Models.EnvironmentProfile named)
                    dots.Add($"presenter={named.Name}");
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                    Visit(VisualTreeHelper.GetChild(element, index));
            }
            Visit(list);
        }
        return $"cache={cached}; dots={string.Join(", ", dots)}";
    }

    private static async Task StopExternallyAsync(string name)
    {
        var escaped = name.Replace("'", "''", StringComparison.Ordinal);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name '{escaped}' -ErrorAction SilentlyContinue; if ($vm -and $vm.State -ne 'Off') {{ Stop-VM -VM $vm -TurnOff -Force -Confirm:$false }}"
            }
        })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        await PumpAsync(600);
    }
}
