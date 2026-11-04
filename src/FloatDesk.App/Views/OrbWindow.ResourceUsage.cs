using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Diagnostics;

namespace FloatDesk.App.Views;

public partial class OrbWindow
{
    private CancellationTokenSource? _resourceCancellation;
    private ResourceUsageSettings _resourceOptions = new();
    private ResourceUsageSnapshot _resourceSnapshot = ResourceUsageSnapshot.Empty;
    private readonly ToolTip _orbResourceTip = new();
    private readonly ToolTip _ringResourceTip = new();

    private void InitializeResourceUsage()
    {
        IsVisibleChanged += (_, _) => RefreshResourceSampling();
        Closed += (_, _) => StopResourceSampling();
        UsageRings.MouseLeftButtonDown += OnMouseLeftButtonDown;
        UsageRings.MouseMove += OnMouseMove;
        UsageRings.MouseLeftButtonUp += OnMouseLeftButtonUp;
        UsageRings.ContextMenu = OrbRoot.ContextMenu;
        OrbRoot.ToolTipOpening += OnResourceTipOpening;
        UsageRings.ToolTipOpening += OnResourceTipOpening;
    }

    private void ApplyResourceUsage(ResourceUsageSettings options)
    {
        var changed = _resourceOptions != options;
        _resourceOptions = options;
        UsageLiquid.Configure(options, _sizeDip);
        var ringExtent = ResourceUsageSurface.RingExtent(_sizeDip, options.RingThicknessPercent);
        UsageRings.Width = UsageRings.Height = _sizeDip + 2 * ringExtent;
        Canvas.SetLeft(UsageRings, -ringExtent);
        Canvas.SetTop(UsageRings, -ringExtent);
        UsageRings.Configure(options, _sizeDip);
        var delay = double.IsFinite(options.HoverDelaySeconds) ? Math.Clamp(options.HoverDelaySeconds, .2, 5) : .8;
        foreach (var element in new FrameworkElement[] { OrbRoot, UsageRings })
        {
            ToolTipService.SetInitialShowDelay(element, (int)(delay * 1000));
            ToolTipService.SetBetweenShowDelay(element, 0);
            ToolTipService.SetShowDuration(element, 30000);
        }
        if (options.Enabled)
        {
            OrbRoot.ToolTip = options.ShowDetails ? _orbResourceTip : null;
            UsageRings.ToolTip = options.ShowDetails ? _ringResourceTip : null;
            RefreshResourceDetails();
        }
        else
        {
            CloseResourceDetails();
            OrbRoot.ToolTip = "单击打开快捷面板，拖动移动";
            UsageRings.ToolTip = null;
        }
        if (changed)
        {
            StopResourceSampling(); _resourceSnapshot = ResourceUsageSnapshot.Empty;
            UsageLiquid.SetSample(_resourceSnapshot); UsageRings.SetSample(_resourceSnapshot);
        }
        RefreshResourceSampling();
    }

    private void RefreshResourceSampling()
    {
        UsageLiquid.SetSuspended(!IsVisible || _edgeCollapsed);
        UsageRings.SetSuspended(!IsVisible || _edgeCollapsed);
        if (!IsVisible || _edgeCollapsed || !_resourceOptions.Enabled) { StopResourceSampling(); return; }
        if (_resourceCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _resourceCancellation = cancellation;
        var includeGpu = (_resourceOptions.LiquidEnabled && _resourceOptions.LiquidMetric == ResourceMetric.Gpu) ||
            (_resourceOptions.RingsEnabled && _resourceOptions.RingGpu) || _resourceOptions.ShowDetails;
        _ = Task.Run(async () =>
        {
            try
            {
                using var sampler = new ResourceUsageSampler();
                while (!cancellation.IsCancellationRequested)
                {
                    ResourceUsageSnapshot snapshot;
                    try { snapshot = sampler.Sample(includeGpu); }
                    catch { snapshot = ResourceUsageSnapshot.Empty; }
                    if (cancellation.IsCancellationRequested) break;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_resourceCancellation != cancellation || cancellation.IsCancellationRequested) return;
                        _resourceSnapshot = snapshot;
                        UsageLiquid.SetSample(snapshot); UsageRings.SetSample(snapshot);
                        RefreshResourceDetails();
                        if (_edgeCollapsed || OrbRoot.IsMouseCaptured || _panelOpenState?.Invoke() == true) CloseResourceDetails();
                    }, DispatcherPriority.Background, cancellation.Token);
                    await Task.Delay(1000, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { /* Dispatcher has shut down. */ }
            finally { cancellation.Dispose(); }
        });
    }

    private void StopResourceSampling()
    {
        var cancellation = _resourceCancellation;
        _resourceCancellation = null;
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* Sampling finished while this UI update was queued. */ }
        CloseResourceDetails();
    }

    private void OnResourceTipOpening(object sender, ToolTipEventArgs e)
    {
        if (_resourceOptions.Enabled && (!_resourceOptions.ShowDetails || _edgeCollapsed ||
            OrbRoot.IsMouseCaptured || _panelOpenState?.Invoke() == true)) e.Handled = true;
    }
    private void CloseResourceDetails() { _orbResourceTip.IsOpen = false; _ringResourceTip.IsOpen = false; }

    private void RefreshResourceDetails()
    {
        static string Percent(double? p) => p is { } n && double.IsFinite(n) ? $"{n:F1}%" : "不可用";
        var s = _resourceSnapshot;
        var gpu = s.SelectGpu(_resourceOptions.GpuDeviceId);
        var memory = s.TotalMemoryBytes > 0 ? $"（{s.UsedMemoryBytes / 1073741824d:F1} / {s.TotalMemoryBytes / 1073741824d:F1} GB）" : "";
        var text = $"CPU：{Percent(s.CpuPercent)}\n内存：{Percent(s.MemoryPercent)}{memory}\n显卡：{gpu?.Name ?? "不可用"} · {Percent(gpu?.Percent)}";
        _orbResourceTip.Content = _ringResourceTip.Content = text;
    }
}
