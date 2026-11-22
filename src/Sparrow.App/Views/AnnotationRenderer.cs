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
                Draw(canvas, item, paint, bitmap);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static void Draw(SKCanvas canvas, IAnnotation item, SKPaint paint, SKBitmap source)
    {
        switch (item)
        {
            case ShapeAnnotation shape:
                DrawShape(canvas, shape, paint);
                break;

            case ArrowAnnotation arrow:
                DrawArrow(canvas, arrow, paint);
                break;

            case LineAnnotation line:
                DrawStraightLine(canvas, line, paint);
                break;

            case SpotlightAnnotation spotlight:
                DrawSpotlight(canvas, spotlight);
                break;

            case MagnifierAnnotation magnifier:
                DrawMagnifier(canvas, magnifier, paint, source);
                break;

            case PenAnnotation pen:
                if (pen.Points.Count < 2)
                    break;

                using (var effect = DashEffect(pen.Dash, pen.Width))
                {
                    paint.PathEffect = effect;
                    paint.Color = WithOpacity(pen.Color, pen.Opacity);
                    paint.StrokeWidth = (float)pen.Width;
                    paint.Style = SKPaintStyle.Stroke;

                    using var path = new SKPath();
                    path.MoveTo((float)pen.Points[0].X, (float)pen.Points[0].Y);
                    for (var index = 1; index < pen.Points.Count; index++)
                        path.LineTo((float)pen.Points[index].X, (float)pen.Points[index].Y);

                    canvas.DrawPath(path, paint);
                    paint.PathEffect = null;
                }
                break;

            case MosaicAnnotation mosaic:
                DrawMosaic(canvas, mosaic, source);
                break;

            case TextAnnotation text:
                if (string.IsNullOrEmpty(text.Text))
                    break;

                var fontStyle = text.Bold ? SKFontStyle.Bold : SKFontStyle.Normal;
                using (var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", fontStyle) ?? SKTypeface.Default)
                using (var font = new SKFont(typeface, (float)text.FontSize))
                {
                    paint.Color = new SKColor(text.Color);
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawText(
                        text.Text,
                        (float)text.Origin.X,
                        (float)text.Origin.Y,
                        SKTextAlign.Left,
                        font,
                        paint);
                }
                break;

            case NumberAnnotation number:
                DrawNumber(canvas, number, paint);
                break;
        }
    }

    private static void DrawShape(SKCanvas canvas, ShapeAnnotation shape, SKPaint paint)
    {
        var rect = new SKRect(
            (float)shape.Rect.X,
            (float)shape.Rect.Y,
            (float)shape.Rect.Right,
            (float)shape.Rect.Bottom);

        // 圆角不能超过短边的一半，否则 Skia 会画出怪形状
        var radius = (float)Math.Clamp(shape.Radius, 0, Math.Min(shape.Rect.Width, shape.Rect.Height) / 2);

        if (shape.Fill != ShapeFill.None)
        {
            var fillAlpha = shape.Fill == ShapeFill.Solid ? shape.Opacity : shape.Opacity * 0.28;
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = WithOpacity(shape.Color, fillAlpha)
            };

            if (shape.Ellipse)
                canvas.DrawOval(rect, fill);
            else if (radius > 0)
                canvas.DrawRoundRect(rect, radius, radius, fill);
            else
                canvas.DrawRect(rect, fill);
        }

        using var effect = DashEffect(shape.Dash, shape.Width);
        paint.PathEffect = effect;
        paint.Color = WithOpacity(shape.Color, shape.Opacity);
        paint.StrokeWidth = (float)shape.Width;
        paint.Style = SKPaintStyle.Stroke;

        if (shape.Ellipse)
            canvas.DrawOval(rect, paint);
        else if (radius > 0)
            canvas.DrawRoundRect(rect, radius, radius, paint);
        else
            canvas.DrawRect(rect, paint);

        paint.PathEffect = null;
    }

    /// <summary>放大镜：在原矩形里显示「中心 1/Zoom 区域」的放大结果（镜片不动，画面放大）。</summary>
    private static void DrawMagnifier(SKCanvas canvas, MagnifierAnnotation magnifier, SKPaint paint,
        SKBitmap source)
    {
        var rect = magnifier.Rect;
        if (rect.Width < 4 || rect.Height < 4)
            return;

        var zoom = Math.Clamp(magnifier.Zoom, 1.2, 6);
        var dest = new SKRect(
            (float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

        // 源区域 = 目标区域按 1/zoom 从中心收缩
        var sourceWidth = rect.Width / zoom;
        var sourceHeight = rect.Height / zoom;
        var sourceRect = new Rect(
            rect.X + rect.Width / 2 - sourceWidth / 2,
            rect.Y + rect.Height / 2 - sourceHeight / 2,
            sourceWidth, sourceHeight);

        var matrix = canvas.TotalMatrix;
        var topLeft = matrix.MapPoint(new SKPoint((float)sourceRect.X, (float)sourceRect.Y));
        var bottomRight = matrix.MapPoint(new SKPoint(
            (float)sourceRect.Right, (float)sourceRect.Bottom));
        var src = new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);

        src = new SKRect(
            Math.Max(0, src.Left),
            Math.Max(0, src.Top),
            Math.Min(source.Width, src.Right),
            Math.Min(source.Height, src.Bottom));

        if (src.Width < 1 || src.Height < 1)
            return;

        canvas.Save();

        if (magnifier.Circle)
        {
            using var clip = new SKPath();
            clip.AddOval(dest);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(dest);
        }

        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(
                image, src, dest,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        canvas.Restore();

        // 边框：圆形描边椭圆，方形描边矩形
        paint.Color = new SKColor(magnifier.BorderColor);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 2;
        paint.PathEffect = null;

        if (magnifier.Circle)
            canvas.DrawOval(dest, paint);
        else
            canvas.DrawRect(dest, paint);
    }

    /// <summary>聚光灯：用一个巨大的外框 + 内洞组成 EvenOdd 路径，一次填充完成压暗。</summary>
    private static void DrawSpotlight(SKCanvas canvas, SpotlightAnnotation spotlight)
    {
        var rect = new SKRect(
            (float)spotlight.Rect.X,
            (float)spotlight.Rect.Y,
            (float)spotlight.Rect.Right,
            (float)spotlight.Rect.Bottom);

        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(-10000, -10000, 10000, 10000));
        if (spotlight.Ellipse)
            path.AddOval(rect);
        else
            path.AddRect(rect);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = WithOpacity(spotlight.Color, spotlight.Opacity)
        };

        canvas.DrawPath(path, paint);
    }

    private static void DrawStraightLine(SKCanvas canvas, LineAnnotation line, SKPaint paint)
    {
        using var effect = DashEffect(line.Dash, line.Width);
        paint.PathEffect = effect;
        paint.Color = WithOpacity(line.Color, line.Opacity);
        paint.StrokeWidth = (float)line.Width;
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(
            (float)line.From.X,
            (float)line.From.Y,
            (float)line.To.X,
            (float)line.To.Y,
            paint);
        paint.PathEffect = null;
    }

    private static void DrawArrow(SKCanvas canvas, ArrowAnnotation arrow, SKPaint paint)    {
        using (var effect = DashEffect(arrow.Dash, arrow.Width))
        {
            paint.PathEffect = effect;
            paint.Color = WithOpacity(arrow.Color, arrow.Opacity);
            paint.StrokeWidth = (float)arrow.Width;
            paint.Style = SKPaintStyle.Stroke;
            canvas.DrawLine(
                (float)arrow.From.X,
                (float)arrow.From.Y,
                (float)arrow.To.X,
                (float)arrow.To.Y,
                paint);
            paint.PathEffect = null;
        }

        var (left, right) = ArrowGeometry.Head(
            arrow.From,
            arrow.To,
            Math.Max(8, arrow.Width * 3.5));

        using var head = new SKPath();
        head.MoveTo((float)arrow.To.X, (float)arrow.To.Y);
        head.LineTo((float)left.X, (float)left.Y);
        head.LineTo((float)right.X, (float)right.Y);
        head.Close();

        paint.Color = WithOpacity(arrow.Color, arrow.Opacity);
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawPath(head, paint);
    }

    /// <summary>虚线 / 点线：dash 长度跟着线宽走，粗细变化时观感一致。</summary>
    private static SKPathEffect? DashEffect(LineDash dash, double width)
    {
        var w = (float)Math.Max(1, width);

        return dash switch
        {
            LineDash.Dash => SKPathEffect.CreateDash([w * 3, w * 2], 0),
            LineDash.Dot => SKPathEffect.CreateDash([w * 0.1f, w * 1.8f], 0),
            _ => null
        };
    }

    /// <summary>马赛克：像素化用「缩小再最近邻放大」，模糊用 Skia 的模糊滤镜。</summary>
    private static void DrawMosaic(SKCanvas canvas, MosaicAnnotation mosaic, SKBitmap source)
    {
        var rect = new SKRect(
            (float)mosaic.Rect.X,
            (float)mosaic.Rect.Y,
            (float)mosaic.Rect.Right,
            (float)mosaic.Rect.Bottom);

        // 目标区域换算到源图坐标（画布此时已按 scale 缩放）
        var matrix = canvas.TotalMatrix;
        var sourceLeft = matrix.MapPoint(new SKPoint(rect.Left, rect.Top));
        var sourceRight = matrix.MapPoint(new SKPoint(rect.Right, rect.Bottom));
        var sourceRect = new SKRect(
            Math.Max(0, sourceLeft.X),
            Math.Max(0, sourceLeft.Y),
            Math.Min(source.Width, sourceRight.X),
            Math.Min(source.Height, sourceRight.Y));

        if (sourceRect.Width < 1 || sourceRect.Height < 1)
            return;

        canvas.Save();
        canvas.ClipRect(rect);

        if (mosaic.Blur)
        {
            using var filter = SKImageFilter.CreateBlur(8, 8);
            using var paint = new SKPaint { ImageFilter = filter };
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, paint);
        }
        else
        {
            var cell = Math.Max(2, mosaic.Cell);
            var columns = Math.Max(1, (int)(sourceRect.Width / cell));
            var rows = Math.Max(1, (int)(sourceRect.Height / cell));

            using var sourceImage = SKImage.FromBitmap(source);
            using var small = new SKBitmap(columns, rows);

            // 线性采样：一格之内的像素要被平均，否则棋盘格缩小后还是黑白
            using (var smallCanvas = new SKCanvas(small))
            {
                smallCanvas.DrawImage(
                    sourceImage,
                    sourceRect,
                    new SKRect(0, 0, columns, rows),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            using var image = SKImage.FromBitmap(small);
            canvas.DrawImage(
                image,
                new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        canvas.Restore();
    }

    private static void DrawNumber(SKCanvas canvas, NumberAnnotation number, SKPaint paint)
    {
        var radius = (float)(number.Size / 2);
        var center = new SKPoint((float)number.Center.X, (float)number.Center.Y);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(number.Color);
        canvas.DrawCircle(center, radius, paint);

        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)(number.Size * 0.55));
        paint.Color = SKColors.White;

        var text = number.Number.ToString();
        var width = font.MeasureText(text);
        canvas.DrawText(
            text,
            center.X - width / 2,
            center.Y + (float)(number.Size * 0.2),
            SKTextAlign.Left,
            font,
            paint);
    }

    private static SKColor WithOpacity(uint argb, double opacity)
    {
        var color = new SKColor(argb);
        var alpha = (byte)Math.Clamp(color.Alpha * Math.Clamp(opacity, 0, 1), 0, 255);
        return color.WithAlpha(alpha);
    }

    /// <summary>把图片四角切成圆角（透明背景）；半径按物理像素给。</summary>
    public static byte[] RoundCorners(byte[] png, double radiusPixels)
    {
        if (radiusPixels <= 0.5)
            return png;

        using var source = SKBitmap.Decode(png);
        if (source is null)
            return png;

        using var output = new SKBitmap(source.Width, source.Height);

        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);

            using var path = new SKPath();
            path.AddRoundRect(
                new SKRect(0, 0, source.Width, source.Height),
                (float)radiusPixels,
                (float)radiusPixels);

            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
            canvas.DrawBitmap(source, 0, 0);
        }

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
