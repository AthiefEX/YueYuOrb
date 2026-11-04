using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

internal static class ResourceUsageGeometry
{
    internal static double LiquidLevel(double percent)
    {
        var fraction = Math.Clamp(percent / 100, 0, 1);
        var low = -1d; var high = 1d;
        for (var i = 0; i < 45; i++)
        {
            var d = (low + high) / 2;
            var area = (Math.Acos(d) - d * Math.Sqrt(Math.Max(0, 1 - d * d))) / Math.PI;
            if (area > fraction) low = d; else high = d;
        }
        return ((low + high) / 2 + 1) / 2;
    }
    internal static double SegmentSweep(int count) => count <= 1 ? 360 : 360d / count - 8;
    internal static double ThicknessPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 2, 8) : 4.3;
    internal static double RingRadius(double size, double thickness) => size * (.522 + ThicknessPercent(thickness) / 200);
    internal static double SegmentSweep(int count, double thickness) => count <= 1 ? 360 :
        360d / count - Math.Max(8, ThicknessPercent(thickness) / RingRadius(100, thickness) * 180 / Math.PI + 3);
    internal static bool IsWarning(double percent) => percent >= 90;
}

public sealed class ResourceUsageSurface : FrameworkElement
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ResourceUsageSettings _options = new();
    private ResourceUsageSnapshot _sample = ResourceUsageSnapshot.Empty;
    private readonly Dictionary<ResourceMetric, double> _previous = [];
    private readonly Dictionary<ResourceMetric, double> _current = [];
    private double _sampleTime;
    private double _lastFrame;
    private bool _subscribed;
    private bool _suspended;
    public bool RingsOnly { get; set; }
    public double SphereSize { get; set; } = 56;
    internal static double RingExtent(double size, double thickness = 4.3) => size * (.032 + ResourceUsageGeometry.ThicknessPercent(thickness) / 100);

    public ResourceUsageSurface()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public void Configure(ResourceUsageSettings options, double size)
    {
        _options = options;
        SphereSize = size;
        IsHitTestVisible = RingsOnly && options.RingsEnabled;
        Visibility = (RingsOnly ? options.RingsEnabled : options.LiquidEnabled) ? Visibility.Visible : Visibility.Collapsed;
        if (!options.Enabled) { _sample = ResourceUsageSnapshot.Empty; _current.Clear(); _previous.Clear(); }
        UpdateAnimation(); InvalidateVisual();
    }

    public void SetSample(ResourceUsageSnapshot sample)
    {
        foreach (var metric in Enum.GetValues<ResourceMetric>())
        {
            var old = DisplayValue(metric);
            var value = sample.Get(metric, _options.GpuDeviceId);
            if (value is { } number && double.IsFinite(number))
            {
                _previous[metric] = old ?? Math.Clamp(number, 0, 100);
                _current[metric] = Math.Clamp(number, 0, 100);
            }
            else { _previous.Remove(metric); _current.Remove(metric); }
        }
        _sample = sample; _sampleTime = _clock.Elapsed.TotalSeconds;
        UpdateAnimation(); InvalidateVisual();
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        UpdateAnimation();
    }

    private double? DisplayValue(ResourceMetric metric)
    {
        if (!_current.TryGetValue(metric, out var target)) return null;
        var t = SystemParameters.ClientAreaAnimation ? Math.Clamp((_clock.Elapsed.TotalSeconds - _sampleTime) / .35, 0, 1) : 1;
        t = t * t * (3 - 2 * t);
        return _previous.GetValueOrDefault(metric, target) * (1 - t) + target * t;
    }

    private void UpdateAnimation()
    {
        var needed = !_suspended && IsLoaded && IsVisible && _options.Enabled && SystemParameters.ClientAreaAnimation;
        if (!needed) { StopAnimation(); return; }
        if (_subscribed) return;
        CompositionTarget.Rendering += OnFrame; _subscribed = true;
    }
    private void StopAnimation()
    {
        if (_subscribed) CompositionTarget.Rendering -= OnFrame;
        _subscribed = false;
    }
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _lastFrame < 1d / 30) return;
        _lastFrame = now;
        if (!RingsOnly || now - _sampleTime < .4) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = SphereSize / 2;
        if (r <= 0) return;
        if (RingsOnly) DrawRings(drawingContext, center, r); else DrawLiquid(drawingContext, center, r);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (!RingsOnly || !_options.RingsEnabled) return null;
        var p = hitTestParameters.HitPoint;
        var radius = ResourceUsageGeometry.RingRadius(SphereSize, _options.RingThicknessPercent);
        var distance = Math.Sqrt(Math.Pow(p.X - ActualWidth / 2, 2) + Math.Pow(p.Y - ActualHeight / 2, 2));
        return Math.Abs(distance - radius) <= SphereSize * (ResourceUsageGeometry.ThicknessPercent(_options.RingThicknessPercent) / 200 + .005) ? new PointHitTestResult(this, p) : null;
    }

    private void DrawLiquid(DrawingContext dc, Point center, double r)
    {
        if (!_options.LiquidEnabled || DisplayValue(_options.LiquidMetric) is not { } value || value <= 0) return;
        var color = UsageColor(_options.LiquidColor, _sample.Get(_options.LiquidMetric, _options.GpuDeviceId));
        color.A = (byte)(color.A * .53);
        var level = center.Y - r + ResourceUsageGeometry.LiquidLevel(value) * r * 2;
        var wave = value is > 1 and < 99 && SystemParameters.ClientAreaAnimation ? r * .022 : 0;
        var phase = _clock.Elapsed.TotalSeconds * 1.4;
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(center.X - r, center.Y + r + 1), true, true);
            for (var i = 0; i <= 32; i++)
            {
                var x = center.X - r + i * r / 16;
                var y = level + wave * Math.Sin(i * Math.PI / 16 + phase);
                g.LineTo(new Point(x, y), true, false);
            }
            g.LineTo(new Point(center.X + r, center.Y + r + 1), true, false);
        }
        shape.Freeze();
        dc.PushClip(new EllipseGeometry(center, r, r));
        dc.DrawGeometry(FrozenBrush(color), null, shape);
        dc.Pop();
    }

    private void DrawRings(DrawingContext dc, Point center, double r)
    {
        if (!_options.RingsEnabled) return;
        var metrics = new List<(ResourceMetric Metric, string Color)>();
        if (_options.RingMemory) metrics.Add((ResourceMetric.Memory, _options.MemoryColor));
        if (_options.RingCpu) metrics.Add((ResourceMetric.Cpu, _options.CpuColor));
        if (_options.RingGpu) metrics.Add((ResourceMetric.Gpu, _options.GpuColor));
        var thickness = SphereSize * ResourceUsageGeometry.ThicknessPercent(_options.RingThicknessPercent) / 100;
        var radius = ResourceUsageGeometry.RingRadius(SphereSize, _options.RingThicknessPercent);
        for (var i = 0; i < metrics.Count; i++)
        {
            var sweep = ResourceUsageGeometry.SegmentSweep(metrics.Count, _options.RingThicknessPercent);
            var gap = 360d / metrics.Count - sweep;
            var start = -90 + i * 360d / metrics.Count + gap / 2;
            DrawArc(dc, center, radius, start, sweep, thickness, Color.FromArgb(65, 210, 225, 240));
            if (DisplayValue(metrics[i].Metric) is not { } value || value <= 0) continue;
            DrawArc(dc, center, radius, start, sweep * value / 100, thickness,
                UsageColor(metrics[i].Color, _sample.Get(metrics[i].Metric, _options.GpuDeviceId)));
        }
    }
    private static void DrawArc(DrawingContext dc, Point c, double r, double start, double sweep, double thickness, Color color)
    {
        if (sweep <= 0) return;
        var pen = new Pen(FrozenBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (sweep >= 359.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        Point At(double degrees) => new(c.X + r * Math.Cos(degrees * Math.PI / 180), c.Y + r * Math.Sin(degrees * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(start), false, false);
            g.ArcTo(At(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
    }
    private static Color UsageColor(string text, double? percent)
    {
        if (percent is { } p && ResourceUsageGeometry.IsWarning(p)) return Color.FromRgb(255, 76, 83);
        try { return (Color)ColorConverter.ConvertFromString(text); }
        catch { return Color.FromRgb(89, 196, 166); }
    }
    private static SolidColorBrush FrozenBrush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
}
