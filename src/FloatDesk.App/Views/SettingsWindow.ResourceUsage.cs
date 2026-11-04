using FloatDesk.Core.Models;
using FloatDesk.Windows.Diagnostics;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private IReadOnlyList<GpuUsage> _resourceGpuDevices = [];
    private bool _resourceGpuLoading;

    private void InitializeResourceUsageSettings()
    {
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible || _resourceGpuLoading) return;
            _resourceGpuLoading = true;
            try
            {
                _resourceGpuDevices = await Task.Run(() =>
                {
                    using var sampler = new ResourceUsageSampler();
                    return sampler.Sample().Gpus;
                });
                _viewModel.SetResourceGpuDevices(_resourceGpuDevices);
            }
            catch { _viewModel.SetResourceGpuDevices([]); }
            finally { _resourceGpuLoading = false; }
        };
    }
}
