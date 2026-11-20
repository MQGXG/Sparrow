using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

/// <summary>
/// 把标注合成进 PNG。
///
/// 用 Skia 而不是 Avalonia 的渲染截图：图元是 DIP 坐标，导出时按缩放乘上去，
/// 高 DPI 下线条不会发虚。项目本来就依赖 SkiaSharp（OCR、SVG 也在用）。
/// </summary>
internal static class AnnotationRenderer
{
    /// <summary>合成后的 PNG；没有标注时原样返回。</summary>
    public static byte[] Compose(byte[] png, IReadOnlyList<IAnnotation> items, double scale)
    {
        if (items.Count == 0)
            return png;

        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
            return png;

        using (var canvas = new SKCanvas(bitmap))
        {
            if (scale > 0 && Math.Abs(scale - 1) > 0.0001)
                canvas.Scale((float)scale);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            foreach (var item in items)
                Draw(canvas, item, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static void Draw(SKCanvas canvas, IAnnotation item, SKPaint paint)
    {
        switch (item)
        {
            case ShapeAnnotation shape:
                paint.Color = new SKColor(shape.Color);
                paint.StrokeWidth = (float)shape.Width;
                paint.Style = SKPaintStyle.Stroke;

                var rect = new SKRect(
                    (float)shape.Rect.X,
                    (float)shape.Rect.Y,
                    (float)shape.Rect.Right,
                    (float)shape.Rect.Bottom);

                if (shape.Ellipse)
                    canvas.DrawOval(rect, paint);
                else
                    canvas.DrawRect(rect, paint);
                break;

            case ArrowAnnotation arrow:
                paint.Color = new SKColor(arrow.Color);
                paint.StrokeWidth = (float)arrow.Width;
                paint.Style = SKPaintStyle.Stroke;
                canvas.DrawLine(
                    (float)arrow.From.X,
                    (float)arrow.From.Y,
                    (float)arrow.To.X,
                    (float)arrow.To.Y,
                    paint);

                var (left, right) = ArrowGeometry.Head(
                    arrow.From,
                    arrow.To,
                    Math.Max(8, arrow.Width * 3.5));

                using (var head = new SKPath())
                {
                    head.MoveTo((float)arrow.To.X, (float)arrow.To.Y);
                    head.LineTo((float)left.X, (float)left.Y);
                    head.LineTo((float)right.X, (float)right.Y);
                    head.Close();

                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(head, paint);
                }
                break;

            case PenAnnotation pen:
                if (pen.Points.Count < 2)
                    break;

                paint.Color = new SKColor(pen.Color);
                paint.StrokeWidth = (float)pen.Width;
                paint.Style = SKPaintStyle.Stroke;

                using (var path = new SKPath())
                {
                    path.MoveTo((float)pen.Points[0].X, (float)pen.Points[0].Y);
                    for (var index = 1; index < pen.Points.Count; index++)
                        path.LineTo((float)pen.Points[index].X, (float)pen.Points[index].Y);

                    canvas.DrawPath(path, paint);
                }
                break;
        }
    }
}
