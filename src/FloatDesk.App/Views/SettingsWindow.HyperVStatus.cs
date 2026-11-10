using System.Windows.Controls;
using System.Windows.Threading;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private bool _vmRefreshRunning;
    private bool _vmPollRefresh;
    private void InitializeVmStatusPolling(StackPanel panel)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) =>
        {
            if (!panel.IsVisible) return;
            // Re-evaluate the dots even when a refresh is already in flight or skipped: a snapshot
            // that is no longer being renewed must stop looking like a live running machine.
            RefreshEnvironmentStateIndicators();
            if (_vmRefreshRunning) return;
            _vmPollRefresh = true;
            try { await RefreshHyperVVmsAsync(); }
            finally { _vmPollRefresh = false; }
        };
        panel.IsVisibleChanged += (_, _) =>
        {
            if (panel.IsVisible)
            {
                timer.Start();
                // Painting the page from the last snapshot made a stopped machine look like it was
                // still running until the first tick three seconds later; ask the host right away.
                _ = RefreshHyperVVmsAsync();
            }
            else timer.Stop();
        };
        panel.Unloaded += (_, _) => timer.Stop();
        if (panel.IsVisible) timer.Start();
    }
}
