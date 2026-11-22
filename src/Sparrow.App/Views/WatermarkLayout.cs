using Avalonia;

namespace Sparrow.App.Views;

/// <summary>
/// 水印的平铺落点：预览和导出共用，保证两边位置一致。
///
/// 平铺范围是区域对角线长度的正方形（旋转任意角度都能盖满），
/// 数量超过上限时先把间距放大再平铺，而不是直接跳过——
/// 跳过会让水印“凭空消失”，放大间距至少还能看见。
/// </summary>
internal static class WatermarkLayout
{
    /// <summary>预览最多画这么多条，避免大选区生成上百个 TextBlock。</summary>
    public const int PreviewCap = 200;

    /// <summary>导出上限；超过后由 FitSpacing 放大间距。</summary>
    public const int TileCap = 2000;

    public const double MinSpacing = 24;

    /// <summary>把间距放大到平铺数不超过 TileCap；返回实际使用的间距。</summary>
    public static double FitSpacing(Rect area, double spacing)
    {
        spacing = Math.Max(MinSpacing, spacing);
        if (area.Width <= 0 || area.Height <= 0)
            return spacing;

        var diagonal = Diagonal(area);
        while (Columns(diagonal, spacing) * Columns(diagonal, spacing) > TileCap)
            spacing *= 1.25;

        return spacing;
    }

    public static int Count(Rect area, double spacing)
    {
        var columns = Columns(Diagonal(area), spacing);
        return columns * columns;
    }

    /// <summary>行优先给出平铺中心；最多 max 个。</summary>
    public static IEnumerable<Point> Tiles(Rect area, double spacing, int max)
    {
        spacing = Math.Max(MinSpacing, spacing);
        var diagonal = Diagonal(area);
        var columns = Columns(diagonal, spacing);
        var half = diagonal / 2;
        var left = area.Center.X - half;
        var top = area.Center.Y - half;

        var produced = 0;
        for (var row = 0; row < columns && produced < max; row++)
        {
            for (var column = 0; column < columns && produced < max; column++)
            {
                produced++;
                yield return new Point(left + column * spacing, top + row * spacing);
            }
        }
    }

    /// <summary>
    /// 命中测试：只认文字附近的点，不能整个区域都算命中——
    /// 否则铺满选区的水印会抢走“拖动选区”的按下事件。
    /// </summary>
    public static bool Hit(WatermarkAnnotation watermark, Point point, double slop = 5)
    {
        if (string.IsNullOrEmpty(watermark.Text) || !watermark.Area.Contains(point))
            return false;

        var center = watermark.Area.Center;
        var radians = -watermark.Angle * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;

        // 把点转回未旋转的平铺坐标系
        var local = new Point(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);

        // 文字宽度用粗略估算，命中框留 slop；这里只服务交互，不参与导出
        var halfWidth = watermark.FontSize * 0.62 * Math.Max(1, watermark.Text.Length) / 2 + slop;
        var halfHeight = watermark.FontSize * 0.6 + slop;
        var spacing = FitSpacing(watermark.Area, watermark.Spacing);

        foreach (var tile in Tiles(watermark.Area, spacing, TileCap))
        {
            if (Math.Abs(local.X - tile.X) <= halfWidth &&
                Math.Abs(local.Y - tile.Y) <= halfHeight)
                return true;
        }

        return false;
    }

    private static double Diagonal(Rect area) =>
        Math.Sqrt(area.Width * area.Width + area.Height * area.Height);

    private static int Columns(double diagonal, double spacing) =>
        Math.Max(1, (int)Math.Ceiling(diagonal / Math.Max(MinSpacing, spacing)) + 1);
}
