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
}
