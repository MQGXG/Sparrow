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

            case DistanceAnnotation distance:
                DrawDistance(canvas, distance, paint);
                break;

            case AngleAnnotation angle:
                DrawAngle(canvas, angle, paint);
                break;

            case BracketAnnotation bracket:
                DrawBracket(canvas, bracket, paint);
                break;

            case SpeechBubbleAnnotation bubble:
                DrawSpeechBubble(canvas, bubble, paint);
                break;

            case SpotlightAnnotation spotlight:
                DrawSpotlight(canvas, spotlight);
                break;

            case MagnifierAnnotation magnifier:
                DrawMagnifier(canvas, magnifier, paint, source);
                break;

            case WatermarkAnnotation watermark:
                DrawWatermark(canvas, watermark);
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

                using (var font = AnnotationText.CreateFont(text.FontSize, text.Bold))
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

    /// <summary>测距：线身 + 两端垂直刻度 + 中点上方的深色标签。</summary>
    private static void DrawDistance(SKCanvas canvas, DistanceAnnotation distance, SKPaint paint)
    {
        var from = new SKPoint((float)distance.From.X, (float)distance.From.Y);
        var to = new SKPoint((float)distance.To.X, (float)distance.To.Y);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);

        using (var effect = DashEffect(distance.Dash, distance.Width))
        {
            paint.PathEffect = effect;
            paint.Color = WithOpacity(distance.Color, distance.Opacity);
            paint.StrokeWidth = (float)distance.Width;
            paint.Style = SKPaintStyle.Stroke;
            canvas.DrawLine(from, to, paint);
            paint.PathEffect = null;
        }

        if (length < 0.01f)
            return;

        var nx = -dy / length;
        var ny = dx / length;

        // 标签统一放在线的“上方”（屏幕坐标里 y 更小的那一侧）
        if (ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        const float tick = 4;
        paint.Color = WithOpacity(distance.Color, distance.Opacity);
        paint.StrokeWidth = (float)distance.Width;
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(
            from.X - nx * tick, from.Y - ny * tick,
            from.X + nx * tick, from.Y + ny * tick, paint);
        canvas.DrawLine(
            to.X - nx * tick, to.Y - ny * tick,
            to.X + nx * tick, to.Y + ny * tick, paint);

        if (string.IsNullOrEmpty(distance.Label))
            return;

        using var font = AnnotationText.CreateFont(11);
        var textWidth = font.MeasureText(distance.Label);
        var pillWidth = textWidth + (float)(AnnotationText.PaddingX * 2);
        var pillHeight = 11f + (float)(AnnotationText.PaddingY * 2);
        var centerX = (from.X + to.X) / 2 + nx * 12;
        var centerY = (from.Y + to.Y) / 2 + ny * 12;
        var pill = new SKRect(
            centerX - pillWidth / 2, centerY - pillHeight / 2,
            centerX + pillWidth / 2, centerY + pillHeight / 2);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(0xB3000000);
        canvas.DrawRoundRect(pill, 3, 3, paint);

        paint.Color = SKColors.White;
        canvas.DrawText(distance.Label, centerX, centerY + 4, SKTextAlign.Center, font, paint);
    }

    /// <summary>角度：两条射线 + 圆弧 + 弧外侧标签。</summary>
    private static void DrawAngle(SKCanvas canvas, AngleAnnotation angle, SKPaint paint)
    {
        var vertex = new SKPoint((float)angle.Vertex.X, (float)angle.Vertex.Y);
        var a = new SKPoint((float)angle.A.X, (float)angle.A.Y);
        var b = new SKPoint((float)angle.B.X, (float)angle.B.Y);

        var ax = a.X - vertex.X;
        var ay = a.Y - vertex.Y;
        var bx = b.X - vertex.X;
        var by = b.Y - vertex.Y;
        var lengthA = MathF.Sqrt(ax * ax + ay * ay);
        var lengthB = MathF.Sqrt(bx * bx + by * by);

        paint.PathEffect = null;
        paint.Color = WithOpacity(angle.Color, angle.Opacity);
        paint.StrokeWidth = (float)angle.Width;
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(vertex, a, paint);
        canvas.DrawLine(vertex, b, paint);

        var radius = (float)Math.Min(36, Math.Min(lengthA, lengthB) * 0.7);
        if (radius >= 6 && lengthA >= 6 && lengthB >= 6)
        {
            var startAngle = (float)(Math.Atan2(ay, ax) * 180 / Math.PI);
            var endAngle = (float)(Math.Atan2(by, bx) * 180 / Math.PI);
            var sweep = endAngle - startAngle;

            while (sweep > 180)
                sweep -= 360;
            while (sweep < -180)
                sweep += 360;

            var oval = new SKRect(
                vertex.X - radius, vertex.Y - radius,
                vertex.X + radius, vertex.Y + radius);

            using var arc = new SKPath();
            arc.MoveTo(vertex.X + ax / lengthA * radius, vertex.Y + ay / lengthA * radius);
            arc.ArcTo(oval, startAngle, sweep, false);
            canvas.DrawPath(arc, paint);
        }

        if (string.IsNullOrEmpty(angle.Label))
            return;

        var unitAX = lengthA > 0.001f ? ax / lengthA : 1;
        var unitAY = lengthA > 0.001f ? ay / lengthA : 0;
        var unitBX = lengthB > 0.001f ? bx / lengthB : 0;
        var unitBY = lengthB > 0.001f ? by / lengthB : 1;

        var bisectorX = unitAX + unitBX;
        var bisectorY = unitAY + unitBY;
        var bisectorLength = MathF.Sqrt(bisectorX * bisectorX + bisectorY * bisectorY);

        if (bisectorLength < 0.001f)
        {
            bisectorX = -unitAY;
            bisectorY = unitAX;
        }
        else
        {
            bisectorX /= bisectorLength;
            bisectorY /= bisectorLength;
        }

        using var font = AnnotationText.CreateFont(11);
        var textWidth = font.MeasureText(angle.Label);
        var pillWidth = textWidth + (float)(AnnotationText.PaddingX * 2);
        var pillHeight = 11f + (float)(AnnotationText.PaddingY * 2);
        var centerX = vertex.X + bisectorX * (radius + 18);
        var centerY = vertex.Y + bisectorY * (radius + 18);
        var pill = new SKRect(
            centerX - pillWidth / 2, centerY - pillHeight / 2,
            centerX + pillWidth / 2, centerY + pillHeight / 2);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(0xB3000000);
        canvas.DrawRoundRect(pill, 3, 3, paint);

        paint.Color = SKColors.White;
        canvas.DrawText(angle.Label, centerX, centerY + 4, SKTextAlign.Center, font, paint);
    }

    /// <summary>括号：四点折线；方头端点，圆头会显得软。</summary>
    private static void DrawBracket(SKCanvas canvas, BracketAnnotation bracket, SKPaint paint)
    {
        var points = AnnotationGeometry.BracketPoints(bracket);
        if (points.Length < 2)
            return;

        using var effect = DashEffect(bracket.Dash, bracket.Width);
        paint.PathEffect = effect;
        paint.Color = WithOpacity(bracket.Color, bracket.Opacity);
        paint.StrokeWidth = (float)bracket.Width;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeCap = SKStrokeCap.Butt;
        paint.StrokeJoin = SKStrokeJoin.Miter;

        using var path = new SKPath();
        path.MoveTo((float)points[0].X, (float)points[0].Y);
        for (var index = 1; index < points.Length; index++)
            path.LineTo((float)points[index].X, (float)points[index].Y);

        canvas.DrawPath(path, paint);

        // 还原共享画笔，避免影响后续标注
        paint.PathEffect = null;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
    }

    private static void DrawSpeechBubble(SKCanvas canvas, SpeechBubbleAnnotation bubble, SKPaint paint)
    {
        var rect = new SKRect(
            (float)bubble.Rect.X,
            (float)bubble.Rect.Y,
            (float)bubble.Rect.Right,
            (float)bubble.Rect.Bottom);

        if (rect.Width < 4 || rect.Height < 4)
            return;

        var radius = (float)Math.Min(10, Math.Min(bubble.Rect.Width, bubble.Rect.Height) / 2);
        var (a, b) = AnnotationGeometry.BubbleTailBase(bubble.Rect, bubble.Tail);
        var tip = new SKPoint((float)bubble.Tail.X, (float)bubble.Tail.Y);
        var first = new SKPoint((float)a.X, (float)a.Y);
        var second = new SKPoint((float)b.X, (float)b.Y);

        if (bubble.Fill != ShapeFill.None)
        {
            var alpha = bubble.Fill == ShapeFill.Solid ? bubble.Opacity : bubble.Opacity * 0.28;
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = WithOpacity(bubble.Color, alpha)
            };

            canvas.DrawRoundRect(rect, radius, radius, fill);

            using var tail = new SKPath();
            tail.MoveTo(first);
            tail.LineTo(tip);
            tail.LineTo(second);
            tail.Close();
            canvas.DrawPath(tail, fill);
        }

        // 描边：矩形 + 尾巴两条边；不画尾巴底边，避免接缝
        paint.PathEffect = null;
        paint.Color = WithOpacity(bubble.Color, bubble.Opacity);
        paint.StrokeWidth = (float)bubble.Width;
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawRoundRect(rect, radius, radius, paint);
        canvas.DrawLine(first, tip, paint);
        canvas.DrawLine(tip, second, paint);

        if (string.IsNullOrWhiteSpace(bubble.Text))
            return;

        using var font = AnnotationText.CreateFont(bubble.FontSize);
        var lines = AnnotationText.Wrap(bubble.Text, bubble.FontSize, rect.Width - 16);
        var lineHeight = (float)(bubble.FontSize * 1.25);
        var total = lineHeight * lines.Count;
        var baseline = rect.MidY - total / 2 + lineHeight * 0.8f;

        paint.Style = SKPaintStyle.Fill;
        paint.Color = bubble.Fill == ShapeFill.Solid
            ? SKColors.White
            : new SKColor(bubble.Color);

        foreach (var line in lines)
        {
            canvas.DrawText(line, rect.MidX, baseline, SKTextAlign.Center, font, paint);
            baseline += lineHeight;
        }
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

    /// <summary>
    /// 水印：裁剪到区域，绕区域中心旋转后按网格平铺文字。
    /// 平铺范围取对角线正方形，任意角度都能盖满；数量上限由 WatermarkLayout 控制。
    /// </summary>
    private static void DrawWatermark(SKCanvas canvas, WatermarkAnnotation watermark)
    {
        if (string.IsNullOrWhiteSpace(watermark.Text) ||
            watermark.Area.Width < 4 || watermark.Area.Height < 4)
            return;

        var area = new SKRect(
            (float)watermark.Area.X,
            (float)watermark.Area.Y,
            (float)watermark.Area.Right,
            (float)watermark.Area.Bottom);

        var spacing = WatermarkLayout.FitSpacing(watermark.Area, watermark.Spacing);

        using var font = AnnotationText.CreateFont(watermark.FontSize, bold: true);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = WithOpacity(watermark.Color, watermark.Opacity)
        };

        canvas.Save();
        canvas.ClipRect(area);
        canvas.RotateDegrees(
            (float)watermark.Angle,
            (area.Left + area.Right) / 2,
            (area.Top + area.Bottom) / 2);

        // 0.35em 的基线偏移约等于把文字垂直居中到格点
        var baseline = (float)(watermark.FontSize * 0.35);
        foreach (var point in WatermarkLayout.Tiles(
                     watermark.Area, spacing, WatermarkLayout.TileCap))
        {
            canvas.DrawText(
                watermark.Text,
                (float)point.X,
                (float)point.Y + baseline,
                SKTextAlign.Center,
                font,
                paint);
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

        using var font = AnnotationText.CreateFont(number.Size * 0.55);
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
