using System.Windows;

namespace FloatDesk.App.Views;

internal static class WheelFoldMotion
{
    // The complete stagger, including the last item, fits inside one duration.
    internal static double ItemProgress(double progress, int index, int count)
    {
        var delay = count > 1 ? .18 * index / (count - 1) : 0;
        var t = Math.Clamp((progress - delay) / (1 - delay), 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    internal static Point Center(Point anchor, Point bottom, Point target, double progress, bool left)
    {
        if (progress <= 0) return bottom;
        if (progress >= 1) return target;
        var direction = left ? -1 : 1;
        var start = bottom - anchor;
        var end = target - anchor;
        var angle = Math.Atan2(start.Y, direction * start.X);
        var endAngle = Math.Atan2(end.Y, direction * end.X);
        var radius = start.Length + (end.Length - start.Length) * progress;
        angle += (endAngle - angle) * progress;
        return anchor + new Vector(direction * Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }
}
