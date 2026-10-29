using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatDesk.Core.Geometry;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class OrbWindow
{
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly OrbEdgeCollapseState _edgeState = new();
    private readonly Stopwatch _edgeClock = Stopwatch.StartNew();
    private readonly ScaleTransform _edgeScale = new(1, 1);
    private bool _edgeCollapsed;
    private int _edgeSide;

    private void InitializeEdgeCollapse()
    {
        OrbLayout.RenderTransform = _edgeScale;
        _edgeTimer.Tick += OnEdgeTick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _edgeTimer.Start();
            else { _edgeTimer.Stop(); RestoreEdgeOrb(animate: false); }
        };
        EdgeStrip.MouseEnter += (_, _) => RestoreEdgeOrb(animate: true);
        Closed += (_, _) =>
        {
            _edgeTimer.Stop();
            _edgeTimer.Tick -= OnEdgeTick;
        };
    }

    private void OnEdgeTick(object? sender, EventArgs e)
    {
        var bounds = GetPhysicalBounds();
        var handle = new WindowInteropHelper(this).Handle;
        var work = DisplayTopology.FromWindow(handle)?.WorkArea;
        var side = work is null ? 0 : Math.Abs(bounds.Left - work.Value.Left) <= 1 ? -1
            : Math.Abs(bounds.Right - work.Value.Right) <= 1 ? 1 : 0;
        var scale = DesktopGeometry.GetWindowDpiScale(handle);
        var hasPointer = GetCursorPos(out var pointer);
        var inside = hasPointer && (_edgeCollapsed
            ? IsInsideEdgeStrip(pointer.X, pointer.Y, bounds, scale)
            : pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom);
        var blocked = !hasPointer || OrbRoot.IsMouseCaptured || _panelOpenState?.Invoke() == true ||
            OrbRoot.ContextMenu?.IsOpen == true;
        var collapse = _edgeState.Update(_settings?.Orb.AutoCollapseAtEdge == true,
            side, inside, blocked, _edgeClock.Elapsed);
        if (_edgeCollapsed && (side != _edgeSide || !collapse))
            RestoreEdgeOrb(animate: inside && !blocked);
        _edgeSide = side;
        if (collapse && !_edgeCollapsed)
        {
            _edgeCollapsed = true;
            EdgeStrip.Height = _sizeDip;
            EdgeStrip.HorizontalAlignment = side < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            EdgeStrip.Visibility = Visibility.Visible;
            OrbRoot.IsHitTestVisible = false;
            OrbLayout.RenderTransformOrigin = new Point(side < 0 ? 0 : 1, 0.5);
            AnimateEdge(0.08, 0, true);
        }
    }

    private bool IsInsideEdgeStrip(int x, int y, PixelRect bounds, double scale)
    {
        var width = 8 * scale;
        var top = bounds.Top + (bounds.Height - _sizeDip * scale) / 2;
        return y >= top && y < top + _sizeDip * scale && x >= bounds.Left && x < bounds.Right &&
            (_edgeSide < 0 ? x < bounds.Left + width : x >= bounds.Right - width);
    }

    private void RestoreEdgeOrb(bool animate)
    {
        _edgeState.Update(false, 0, false, false, _edgeClock.Elapsed);
        _edgeCollapsed = false;
        OrbRoot.IsHitTestVisible = true;
        EdgeStrip.Visibility = Visibility.Collapsed;
        AnimateEdge(1, 1, animate);
    }

    private void AnimateEdge(double scale, double opacity, bool animate)
    {
        var duration = animate && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast
            ? TimeSpan.FromMilliseconds(180) : TimeSpan.Zero;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        _edgeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        OrbLayout.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, duration) { EasingFunction = easing });
    }
}
