using FloatDesk.Core.Models;
using FloatDesk.Windows.Diagnostics;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private IReadOnlyList<GpuUsage> _resourceGpuDevices = [];
    private long _resourceGpuLoadGeneration;
    private CancellationTokenSource? _resourceGpuCancellation;

    private void InitializeResourceUsageSettings()
    {
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible)
            {
                _resourceGpuLoadGeneration++;
                _resourceGpuCancellation?.Cancel();
                return;
            }

            var generation = ++_resourceGpuLoadGeneration;
            _resourceGpuCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _resourceGpuCancellation = cancellation;
            try
            {
                _resourceGpuDevices = await Task.Run(
                    () =>
                    {
                        using var sampler = new ResourceUsageSampler();
                        return sampler.Sample().Gpus;
                    },
                    cancellation.Token);
                if (generation != _resourceGpuLoadGeneration ||
                    cancellation.IsCancellationRequested ||
                    !IsVisible)
                    return;
                if (Dispatcher.CheckAccess())
                    _viewModel.SetResourceGpuDevices(_resourceGpuDevices);
                else
                    await Dispatcher.InvokeAsync(
                        () => _viewModel.SetResourceGpuDevices(
                            _resourceGpuDevices));
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (generation != _resourceGpuLoadGeneration ||
                    cancellation.IsCancellationRequested ||
                    !IsVisible)
                    return;
                if (Dispatcher.CheckAccess())
                    _viewModel.SetResourceGpuDevices([]);
                else
                    await Dispatcher.InvokeAsync(
                        () => _viewModel.SetResourceGpuDevices([]));
            }
            finally
            {
                if (ReferenceEquals(_resourceGpuCancellation, cancellation))
                    _resourceGpuCancellation = null;
                cancellation.Dispose();
            }
        };
    }
}
