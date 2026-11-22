using Avalonia;

namespace Sparrow.App.Views;

/// <summary>标注的纯几何：角度吸附、括号折线，预览/导出/命中测试共用。</summary>
internal static class AnnotationGeometry
{
    /// <summary>把 current 相对 origin 的方向吸附到 stepDegrees 的整数倍，保持长度不变。</summary>
    public static Point SnapToAngle(Point origin, Point current, double stepDegrees = 45)
    {
        var dx = current.X - origin.X;
        var dy = current.Y - origin.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < 0.001)
            return current;

        var step = stepDegrees * Math.PI / 180;
        var snapped = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return new Point(
            origin.X + Math.Cos(snapped) * length,
            origin.Y + Math.Sin(snapped) * length);
    }

    /// <summary>
    /// 括号的四点折线：上臂 → 竖边 → 下臂。
    /// OpenRight = 竖边在左、开口朝右（`[`）；否则竖边在右（`]`）。
    /// </summary>
    public static Point[] BracketPoints(BracketAnnotation bracket)
    {
        var rect = bracket.Rect;
        if (rect.Width <= 0 || rect.Height <= 0)
            return [];

        var x = bracket.OpenRight ? rect.Left : rect.Right;
        var arm = bracket.OpenRight ? x + bracket.Depth : x - bracket.Depth;

        return
        [
            new Point(arm, rect.Top),
            new Point(x, rect.Top),
            new Point(x, rect.Bottom),
            new Point(arm, rect.Bottom)
        ];
    }

    /// <summary>
    /// 两条边的夹角（0–180）。退化边返回 0，调用方用长度阈值决定是否提交。
    /// </summary>
    public static double AngleBetween(Point vertex, Point a, Point b)
    {
        var ax = a.X - vertex.X;
        var ay = a.Y - vertex.Y;
        var bx = b.X - vertex.X;
        var by = b.Y - vertex.Y;
        var lengthA = Math.Sqrt(ax * ax + ay * ay);
        var lengthB = Math.Sqrt(bx * bx + by * by);

        if (lengthA < 0.001 || lengthB < 0.001)
            return 0;

        var cosine = (ax * bx + ay * by) / (lengthA * lengthB);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// 气泡尾尖：拖拽起点一般落在矩形角上，直接画会没有尾巴；
    /// 落在矩形内时沿“中心 → 起点”方向推出 minLength。
    /// </summary>
    public static Point BubbleTail(Rect rect, Point tip, double minLength = 16)
    {
        if (!rect.Contains(tip))
            return tip;

        var center = rect.Center;
        var dx = tip.X - center.X;
        var dy = tip.Y - center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < 0.001)
            return new Point(center.X, rect.Bottom + minLength);

        return new Point(
            tip.X + dx / length * minLength,
            tip.Y + dy / length * minLength);
    }

    /// <summary>三角尾与矩形相接的两个点：投影到最近的一条边，并沿边留出半宽。</summary>
    public static (Point A, Point B) BubbleTailBase(Rect rect, Point tip)
    {
        var center = rect.Center;
        var half = Math.Min(12, Math.Min(rect.Width, rect.Height) * 0.22);

        if (half < 2 || rect.Width < 8 || rect.Height < 8)
            return (center, center);

        var sx = (tip.X - center.X) / Math.Max(1, rect.Width / 2);
        var sy = (tip.Y - center.Y) / Math.Max(1, rect.Height / 2);
        var margin = half + 2;

        if (Math.Abs(sx) >= Math.Abs(sy))
        {
            var x = sx >= 0 ? rect.Right : rect.Left;
            var top = rect.Top + margin;
            var bottom = rect.Bottom - margin;

            if (bottom < top)
                return (new Point(x, center.Y), new Point(x, center.Y));

            var y = Math.Clamp(tip.Y, top, bottom);
            return (new Point(x, y - half), new Point(x, y + half));
        }
        else
        {
            var y = sy >= 0 ? rect.Bottom : rect.Top;
            var left = rect.Left + margin;
            var right = rect.Right - margin;

            if (right < left)
                return (new Point(center.X, y), new Point(center.X, y));

            var x = Math.Clamp(tip.X, left, right);
            return (new Point(x - half, y), new Point(x + half, y));
        }
    }
}
