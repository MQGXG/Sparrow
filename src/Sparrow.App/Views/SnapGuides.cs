using Avalonia;

namespace Sparrow.App.Views;

/// <summary>
/// 吸附计算。纯函数，方便单测。
///
/// 只做「边缘或中心靠近参考线时贴过去」，参考线由调用方给：
/// 选区用的是屏幕边缘 / 中线，标注用的是选区边框和中线。
/// </summary>
public static class SnapGuides
{
    /// <summary>吸附阈值（DIP）。太小容易错过，太大手感发黏。</summary>
    public const double Tolerance = 6;

    /// <summary>单个坐标的吸附；没有命中返回原值。</summary>
    public static (double Value, double? Guide) Snap(
        double value,
        IReadOnlyList<double> guides,
        double tolerance = Tolerance)
    {
        var best = value;
        double? hit = null;
        var bestDistance = tolerance;

        foreach (var guide in guides)
        {
            var distance = Math.Abs(guide - value);
            if (distance > bestDistance)
                continue;

            bestDistance = distance;
            best = guide;
            hit = guide;
        }

        return (best, hit);
    }

    /// <summary>
    /// 移动/拉伸一个矩形时的边缘吸附：左右边缘分别找最近参考线，取更近的那条。
    /// 返回需要整体平移的量（delta）和命中的参考线。
    /// </summary>
    public static (double Delta, double? Guide) SnapEdges(
        double start,
        double end,
        IReadOnlyList<double> guides,
        double tolerance = Tolerance)
    {
        var bestDelta = 0d;
        double? hit = null;
        var bestDistance = tolerance;

        foreach (var guide in guides)
        {
            var toStart = guide - start;
            if (Math.Abs(toStart) <= bestDistance)
            {
                bestDistance = Math.Abs(toStart);
                bestDelta = toStart;
                hit = guide;
            }

            var toEnd = guide - end;
            if (Math.Abs(toEnd) <= bestDistance)
            {
                bestDistance = Math.Abs(toEnd);
                bestDelta = toEnd;
                hit = guide;
            }
        }

        return (bestDelta, hit);
    }

    /// <summary>屏幕边缘 + 中线，用来吸附整个选区。</summary>
    public static IReadOnlyList<double> ScreenGuides(double size) => [0, size / 2, size];

    /// <summary>选区内的参考线（相对选区）：两边 + 中线。</summary>
    public static IReadOnlyList<double> BoxGuides(double size) => [0, size / 2, size];

    /// <summary>屏幕边 + 中线；给了框就再加上框的边和中线（吸附检测到的窗口）。</summary>
    public static IReadOnlyList<double> Combine(double size, Rect? box, bool vertical)
    {
        var list = new List<double>(ScreenGuides(size));

        if (box is { } b)
        {
            list.AddRange(vertical
                ? [b.Left, b.X + b.Width / 2, b.Right]
                : [b.Top, b.Y + b.Height / 2, b.Bottom]);
        }

        return list;
    }

    /// <summary>把标注落点吸附到选区参考线上，并换回窗口坐标。</summary>
    public static (Point Point, double? Vertical, double? Horizontal) SnapToBox(
        Point point,
        Rect box,
        double tolerance = Tolerance)
    {
        var local = new Point(point.X - box.X, point.Y - box.Y);

        var (x, vertical) = Snap(local.X, BoxGuides(box.Width), tolerance);
        var (y, horizontal) = Snap(local.Y, BoxGuides(box.Height), tolerance);

        return (
            new Point(box.X + x, box.Y + y),
            vertical is null ? null : box.X + vertical.Value,
            horizontal is null ? null : box.Y + horizontal.Value);
    }
}
