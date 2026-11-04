using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    private static readonly DependencyProperty WheelFoldProgressProperty = DependencyProperty.Register(
        "WheelFoldProgress", typeof(double), typeof(QuickPanelWindow),
        new PropertyMetadata(0d, (owner, args) => ((QuickPanelWindow)owner).RenderFoldFrame((double)args.NewValue)));
    private int _wheelExpandDurationMs = 400;
    private FoldItem[] _foldItems = [];
    private Point _foldBottom;

    private sealed record FoldItem(Button Button, Point CanvasPosition, Rect Bounds, int ChildIndex,
        Polygon Beam, Geometry? OriginalClip, Brush? OriginalMask);

    private void BeginFoldMotion()
    {
        CompleteFoldMotion();
        var items = GetVisibleWheelButtons()
            .Select(button => (Button: button, Bounds: GetWheelButtonMenuBounds(button)))
            .Where(item => IsInsideWheelViewport(item.Bounds))
            .OrderByDescending(item => item.Bounds.Top)
            .ToArray();
        if (items.Length == 0) return;

        _foldBottom = new Point(items[0].Bounds.Left + items[0].Bounds.Width / 2,
            items[0].Bounds.Top + items[0].Bounds.Height / 2);
        _foldItems = items.Select(item =>
        {
            var beam = CreateActionBeam(item.Button);
            return new FoldItem(item.Button, new Point(Canvas.GetLeft(item.Button), Canvas.GetTop(item.Button)),
                item.Bounds, WheelCanvas.Children.IndexOf(item.Button), beam, item.Button.Clip, item.Button.OpacityMask);
        }).ToArray();

        // Move actual buttons outside the ScrollViewer clipping rectangle during the arc.
        // Restoring the same instances preserves click handlers, icons and accessibility names.
        foreach (var item in _foldItems)
        {
            WheelCanvas.Children.Remove(item.Button);
            WheelEntranceLayer.Children.Add(item.Beam);
        }
        foreach (var item in _foldItems)
        {
            item.Button.BeginAnimation(OpacityProperty, null);
            item.Button.RenderTransform = Transform.Identity;
            ApplyFoldViewportAppearance(item);
            WheelEntranceLayer.Children.Add(item.Button);
        }
        WheelScroller.Opacity = 0;
        ActionBeamsLayer.Visibility = Visibility.Hidden;
        RenderFoldFrame(0);
        SetCurrentValue(WheelFoldProgressProperty, 0d);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(_wheelExpandDurationMs));
        animation.Completed += (_, _) => CompleteFoldMotion();
        BeginAnimation(WheelFoldProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyFoldViewportAppearance(FoldItem item)
    {
        var offset = new Vector(Canvas.GetLeft(WheelScroller) - item.Bounds.Left,
            Canvas.GetTop(WheelScroller) - item.Bounds.Top);
        Geometry viewport = new RectangleGeometry(new Rect(0, 0, WheelViewportWidth, WheelViewportHeight));
        if (WheelScroller.Clip is { } clip)
            viewport = new CombinedGeometry(GeometryCombineMode.Intersect, viewport, clip);
        var local = new GeometryGroup { Transform = new TranslateTransform(offset.X, offset.Y) };
        local.Children.Add(viewport);
        Geometry buttonClip = item.OriginalClip is null ? local :
            new CombinedGeometry(GeometryCombineMode.Intersect, local, item.OriginalClip);
        buttonClip.Freeze();
        item.Button.Clip = buttonClip;
        // Carry the final viewport fade with each item; otherwise the partial top/bottom
        // row abruptly changes opacity when it returns to the ScrollViewer.
        if (WheelScroller.OpacityMask is LinearGradientBrush source)
        {
            var mask = source.Clone();
            mask.MappingMode = BrushMappingMode.Absolute;
            mask.StartPoint = new Point(0, offset.Y);
            mask.EndPoint = new Point(0, offset.Y + WheelViewportHeight);
            mask.Freeze();
            item.Button.OpacityMask = mask;
        }
    }

    private void RenderFoldFrame(double progress)
    {
        for (var index = 0; index < _foldItems.Length; index++)
        {
            var item = _foldItems[index];
            var t = WheelFoldMotion.ItemProgress(progress, index, _foldItems.Length);
            var target = new Point(item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Top + item.Bounds.Height / 2);
            var center = WheelFoldMotion.Center(_wheelAnchor, _foldBottom, target, t, _wheelOpensLeft);
            var left = center.X - item.Bounds.Width / 2;
            Canvas.SetLeft(item.Button, left);
            Canvas.SetTop(item.Button, center.Y - item.Bounds.Height / 2);
            item.Button.Opacity = item.Beam.Opacity = Math.Clamp(t * 8, 0, 1);
            var x = _wheelOpensLeft ? left + item.Bounds.Width : left;
            item.Beam.Points[0] = _wheelAnchor;
            item.Beam.Points[1] = new Point(x, center.Y - 18);
            item.Beam.Points[2] = new Point(x, center.Y + 18);
        }
    }

    private void CompleteFoldMotion()
    {
        var items = _foldItems;
        _foldItems = [];
        BeginAnimation(WheelFoldProgressProperty, null);
        if (items.Length == 0) return;
        foreach (var item in items.OrderBy(item => item.ChildIndex))
        {
            WheelEntranceLayer.Children.Remove(item.Button);
            Canvas.SetLeft(item.Button, item.CanvasPosition.X);
            Canvas.SetTop(item.Button, item.CanvasPosition.Y);
            item.Button.Opacity = 1;
            item.Button.RenderTransform = Transform.Identity;
            item.Button.Clip = item.OriginalClip;
            item.Button.OpacityMask = item.OriginalMask;
            WheelCanvas.Children.Insert(item.ChildIndex, item.Button);
        }
        WheelEntranceLayer.Children.Clear();
        // Offscreen entries must also be ready when the user begins scrolling.
        foreach (var button in GetVisibleWheelButtons())
        {
            button.Opacity = 1;
            button.RenderTransform = Transform.Identity;
        }
        WheelScroller.Opacity = 1;
        ActionBeamsLayer.Visibility = Visibility.Visible;
        UpdateActionBeamGeometry(_wheelAnchor, _wheelOpensLeft ? -1 : 1, GetVisibleWheelButtons());
    }

    private Polygon CreateActionBeam(Button button)
    {
        var beam = new Polygon { IsHitTestVisible = false, Tag = button,
            Effect = new BlurEffect { Radius = 5 },
            Points = new PointCollection { _wheelAnchor, _wheelAnchor, _wheelAnchor } };
        beam.SetResourceReference(Shape.FillProperty, "MenuActionBeamBrush");
        beam.Fill ??= ThemeBrush("MenuActionBeamBrush", Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF));
        return beam;
    }
}
