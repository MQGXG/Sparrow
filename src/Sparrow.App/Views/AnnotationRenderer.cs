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
    /// <summary>合成后的 PNG；没有标注、也没有 OCR 文字标记时原样返回。</summary>
    public static byte[] Compose(
        byte[] png,
        IReadOnlyList<IAnnotation> items,
        double scale,
        IReadOnlyList<OcrTextMark>? textMarks = null)
    {
        var hasMarks = textMarks is { Count: > 0 };

        if (items.Count == 0 && !hasMarks)
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

            // OCR 文字标记画在标注之上：它是"批注文字"，应该压在最上层
            if (hasMarks)
                OcrTextMarks.Draw(canvas, textMarks!);
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
                WithRotation(canvas, shape.Rect.Center, shape.Rotation, () => DrawShape(canvas, shape, paint));
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
                WithRotation(canvas, bracket.Rect.Center, bracket.Rotation,
                    () => DrawBracket(canvas, bracket, paint));
                break;

            case SpeechBubbleAnnotation bubble:
                WithRotation(canvas, bubble.Rect.Center, bubble.Rotation,
                    () => DrawSpeechBubble(canvas, bubble, paint));
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

                paint.Color = WithOpacity(pen.Color, pen.Opacity);
                paint.StrokeWidth = (float)pen.Width;
                paint.Style = SKPaintStyle.Stroke;

                StrokePath(canvas, PenStrokeGeometry(pen.Points), paint, pen.Dash, pen.Style, pen.Width);
                break;

            case MosaicAnnotation mosaic:
                DrawMosaic(canvas, mosaic, source);
                break;

            case TextAnnotation text:
                if (string.IsNullOrEmpty(text.Text))
                    break;

                var textWidth = AnnotationText.Measure(
                    text.Text, text.FontSize, text.Bold, text.FontFamily, text.Italic);
                var textCenter = new Point(
                    text.Origin.X + textWidth / 2,
                    text.Origin.Y - text.FontSize * 0.35);

                WithRotation(canvas, textCenter, text.Rotation, () =>
                    DrawTextAnnotation(canvas, text, textWidth, paint));
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

        using var outline = BuildShapeOutline(rect, radius, shape.Ellipse, shape.Diamond);

        if (shape.Fill != ShapeFill.None)
            DrawShapeFill(canvas, shape, rect, outline);

        paint.Color = WithOpacity(shape.Color, shape.Opacity);
        paint.StrokeWidth = (float)shape.Width;
        paint.Style = SKPaintStyle.Stroke;

        StrokePath(canvas, ShapeStrokeGeometry(rect, radius, shape.Ellipse, shape.Diamond),
            paint, shape.Dash, shape.Style, shape.Width);
    }

    /// <summary>形状轮廓：矩形 / 圆角矩形 / 椭圆 / 菱形，统一成一条路径。</summary>
    private static SKPath BuildShapeOutline(SKRect rect, float radius, bool ellipse, bool diamond)
    {
        var path = new SKPath();

        if (diamond)
        {
            path.MoveTo(rect.MidX, rect.Top);
            path.LineTo(rect.Right, rect.MidY);
            path.LineTo(rect.MidX, rect.Bottom);
            path.LineTo(rect.Left, rect.MidY);
            path.Close();
        }
        else if (ellipse)
        {
            path.AddOval(rect);
        }
        else if (radius > 0)
        {
            path.AddRoundRect(rect, radius, radius);
        }
        else
        {
            path.AddRect(rect);
        }

        return path;
    }

    /// <summary>
    /// 形状填充：实心/淡色直接铺色；斜线/交叉斜线先裁剪到形状，再画 45° 线。
    /// 填充色独立于描边色（FillColor=0 时跟随描边色）。
    /// </summary>
    private static void DrawShapeFill(SKCanvas canvas, ShapeAnnotation shape, SKRect rect, SKPath outline)
    {
        var color = AnnotationStyle.FillColor(shape.Color, shape.FillColor);

        if (shape.Fill is ShapeFill.Line or ShapeFill.CrossLine)
        {
            canvas.Save();
            canvas.ClipPath(outline, SKClipOperation.Intersect, antialias: true);

            using var hatch = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f,
                Color = WithOpacity(color, shape.Opacity)
            };
            DrawHatch(canvas, rect, shape.Fill == ShapeFill.CrossLine, hatch);

            canvas.Restore();
            return;
        }

        var fillAlpha = AnnotationStyle.FillAlpha(shape.Fill, shape.Opacity);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = WithOpacity(color, fillAlpha)
        };

        canvas.DrawPath(outline, fill);
    }

    /// <summary>45° 斜线填充；cross=true 时再叠一组反方向。</summary>
    private static void DrawHatch(SKCanvas canvas, SKRect rect, bool cross, SKPaint paint)
    {
        const float spacing = 8f;

        for (var offset = -rect.Height; offset < rect.Width; offset += spacing)
        {
            canvas.DrawLine(
                rect.Left + offset, rect.Bottom,
                rect.Left + offset + rect.Height, rect.Top,
                paint);
        }

        if (!cross)
            return;

        for (var offset = -rect.Height; offset < rect.Width; offset += spacing)
        {
            canvas.DrawLine(
                rect.Left + offset, rect.Top,
                rect.Left + offset + rect.Height, rect.Bottom,
                paint);
        }
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
        paint.Color = WithOpacity(line.Color, line.Opacity);
        paint.StrokeWidth = (float)line.Width;
        paint.Style = SKPaintStyle.Stroke;

        StrokePath(canvas,
            SegmentStrokeGeometry(
                new SKPoint((float)line.From.X, (float)line.From.Y),
                new SKPoint((float)line.To.X, (float)line.To.Y)),
            paint, line.Dash, line.Style, line.Width);
    }

    /// <summary>测距：线身 + 两端垂直刻度 + 中点上方的深色标签。</summary>
    private static void DrawDistance(SKCanvas canvas, DistanceAnnotation distance, SKPaint paint)
    {
        var from = new SKPoint((float)distance.From.X, (float)distance.From.Y);
        var to = new SKPoint((float)distance.To.X, (float)distance.To.Y);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);

        paint.Color = WithOpacity(distance.Color, distance.Opacity);
        paint.StrokeWidth = (float)distance.Width;
        paint.Style = SKPaintStyle.Stroke;
        StrokePath(canvas, SegmentStrokeGeometry(from, to), paint,
            distance.Dash, distance.Style, distance.Width);

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

        paint.Color = WithOpacity(bracket.Color, bracket.Opacity);
        paint.StrokeWidth = (float)bracket.Width;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeCap = SKStrokeCap.Butt;
        paint.StrokeJoin = SKStrokeJoin.Miter;

        var vertices = new SKPoint[points.Length];
        for (var index = 0; index < points.Length; index++)
            vertices[index] = new SKPoint((float)points[index].X, (float)points[index].Y);

        StrokePath(canvas, PolylineStrokeGeometry(vertices), paint,
            bracket.Dash, bracket.Style, bracket.Width);

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
            var fillColor = AnnotationStyle.FillColor(bubble.Color, bubble.FillColor);

            if (bubble.Fill is ShapeFill.Line or ShapeFill.CrossLine)
            {
                using var hatch = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.4f,
                    Color = WithOpacity(fillColor, bubble.Opacity)
                };

                using var bodyClip = new SKPath();
                bodyClip.AddRoundRect(rect, radius, radius);
                canvas.Save();
                canvas.ClipPath(bodyClip, SKClipOperation.Intersect, antialias: true);
                DrawHatch(canvas, rect, bubble.Fill == ShapeFill.CrossLine, hatch);
                canvas.Restore();

                using var tailClip = new SKPath();
                tailClip.MoveTo(first);
                tailClip.LineTo(tip);
                tailClip.LineTo(second);
                tailClip.Close();
                canvas.Save();
                canvas.ClipPath(tailClip, SKClipOperation.Intersect, antialias: true);
                DrawHatch(canvas, rect, bubble.Fill == ShapeFill.CrossLine, hatch);
                canvas.Restore();
            }
            else
            {
                var alpha = AnnotationStyle.FillAlpha(bubble.Fill, bubble.Opacity);
                using var fill = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = WithOpacity(fillColor, alpha)
                };

                canvas.DrawRoundRect(rect, radius, radius, fill);

                using var tail = new SKPath();
                tail.MoveTo(first);
                tail.LineTo(tip);
                tail.LineTo(second);
                tail.Close();
                canvas.DrawPath(tail, fill);
            }
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

    private static void DrawArrow(SKCanvas canvas, ArrowAnnotation arrow, SKPaint paint)
    {
        paint.Color = WithOpacity(arrow.Color, arrow.Opacity);
        paint.StrokeWidth = (float)arrow.Width;
        paint.Style = SKPaintStyle.Stroke;

        StrokePath(canvas,
            SegmentStrokeGeometry(
                new SKPoint((float)arrow.From.X, (float)arrow.From.Y),
                new SKPoint((float)arrow.To.X, (float)arrow.To.Y)),
            paint, arrow.Dash, arrow.Style, arrow.Width);

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

    /// <summary>描边几何：轮廓顶点 + 是否闭合 + 短边长度（手绘振幅按它算）。</summary>
    private readonly record struct StrokeGeometry(
        IReadOnlyList<SKPoint[]> Contours, bool Closed, float ShortEdge);

    /// <summary>按线型描边：图案决定实线/虚线/点线，笔触决定规整/手绘/涂鸦。</summary>
    private static void StrokePath(SKCanvas canvas, StrokeGeometry geometry, SKPaint paint,
        LineDash dash, LineStyle style, double width)
    {
        paint.PathEffect = null;

        if (style != LineStyle.Neat)
        {
            StrokeSketch(canvas, geometry, paint, dash, style, width);
            return;
        }

        paint.PathEffect = DashEffect(dash, width);
        canvas.DrawPath(BuildStrokePath(geometry), paint);
        paint.PathEffect = null;
    }

    private static SKPath BuildStrokePath(StrokeGeometry geometry)
    {
        var path = new SKPath();

        foreach (var contour in geometry.Contours)
        {
            if (contour.Length == 0)
                continue;

            path.MoveTo(contour[0]);
            for (var index = 1; index < contour.Length; index++)
                path.LineTo(contour[index]);

            if (geometry.Closed)
                path.Close();
        }

        return path;
    }

    /// <summary>
    /// 手绘笔触：把每条原始边当成一段三次贝塞尔，端点不动、控制点沿法线鼓出，
    /// 得到「像人手画上去」的歪线；涂鸦再反向画一遍、幅度减半，形成双线（仅实线）。
    /// </summary>
    private static void StrokeSketch(SKCanvas canvas, StrokeGeometry geometry, SKPaint paint,
        LineDash dash, LineStyle style, double width)
    {
        var amplitude = SketchAmplitude(style, geometry.ShortEdge);
        var passes = style == LineStyle.Rough && dash == LineDash.Solid ? 2 : 1;

        for (var pass = 0; pass < passes; pass++)
        {
            using var varied = BuildSketchPath(geometry, style, pass, amplitude);
            paint.PathEffect = DashEffect(dash, width);
            canvas.DrawPath(varied, paint);
            paint.PathEffect = null;
        }
    }

    /// <summary>
    /// 手绘振幅：上限手绘 2px / 涂鸦 4px，再按图形短边的 10% 收缩，
    /// 避免小图形被抖成一团。
    /// </summary>
    private static float SketchAmplitude(LineStyle style, float shortSide)
    {
        var limit = style == LineStyle.Rough ? 4f : 2f;
        return MathF.Min(limit, MathF.Max(0.5f, shortSide * 0.1f));
    }

    private static SKPath BuildSketchPath(StrokeGeometry geometry, LineStyle style, int pass, float amplitude)
    {
        var options = new SketchOptions(
            Seed: style == LineStyle.Rough ? 0x5EED1234u : 0xA17C0DEu,
            Amplitude: amplitude * (pass == 0 ? 1f : 0.5f),
            FirstPass: pass == 0);

        var result = new SKPath();
        var index = 0;

        foreach (var contour in geometry.Contours)
        {
            if (contour.Length < 2)
                continue;

            result.MoveTo(contour[0]);

            var count = contour.Length;
            var segments = geometry.Closed ? count : count - 1;

            for (var segment = 0; segment < segments; segment++)
            {
                var start = contour[segment];
                var end = contour[(segment + 1) % count];
                var (c1, c2) = SketchControls(start, end, options, index++);
                result.CubicTo(c1, c2, end);
            }

            if (geometry.Closed)
                result.Close();
        }

        return result;
    }

    private readonly record struct SketchOptions(uint Seed, float Amplitude, bool FirstPass);

    /// <summary>一段弓形贝塞尔：端点不动，控制点沿法线鼓出，第二遍反向。</summary>
    private static (SKPoint C1, SKPoint C2) SketchControls(
        SKPoint start, SKPoint end, SketchOptions options, int segment)
    {
        var delta = new SKPoint(end.X - start.X, end.Y - start.Y);
        var span = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        if (span <= 0.0001f)
            return (start, start);

        var nx = -delta.Y / span;
        var ny = delta.X / span;

        var bendSample = Sample(options.Seed ^ 0x632be59bu, segment * 8 + 5);
        var bend = MathF.Sign(bendSample) * (0.88f + 0.04f * MathF.Abs(bendSample)) *
                   (options.FirstPass ? 1f : -1f);

        var jitter1 = options.Amplitude * (bend + 0.06f * Sample(options.Seed, segment * 8 + 1));
        var jitter2 = options.Amplitude * (bend + 0.06f * Sample(options.Seed, segment * 8 + 2));

        return (
            new SKPoint(
                start.X + delta.X * 0.28f + nx * jitter1,
                start.Y + delta.Y * 0.28f + ny * jitter1),
            new SKPoint(
                start.X + delta.X * 0.71f + nx * jitter2,
                start.Y + delta.Y * 0.71f + ny * jitter2));
    }

    /// <summary>形状的轮廓顶点：矩形/菱形 4 点，椭圆/圆角矩形按弧采样。</summary>
    private static StrokeGeometry ShapeStrokeGeometry(SKRect rect, float radius, bool ellipse, bool diamond)
    {
        var shortSide = MathF.Min(rect.Width, rect.Height);

        if (diamond)
        {
            return new StrokeGeometry(
                [
                    new[]
                    {
                        new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.Right, rect.MidY),
                        new SKPoint(rect.MidX, rect.Bottom), new SKPoint(rect.Left, rect.MidY)
                    }
                ],
                true,
                shortSide);
        }

        if (ellipse)
            return new StrokeGeometry([SampleEllipse(rect, 48)], true, shortSide);

        if (radius > 0)
            return new StrokeGeometry([SampleRoundedRect(rect, radius, 8)], true, shortSide);

        return new StrokeGeometry(
            [
                new[]
                {
                    new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
                    new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom)
                }
            ],
            true,
            shortSide);
    }

    private static SKPoint[] SampleEllipse(SKRect rect, int count)
    {
        var points = new SKPoint[count];
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        var radiusX = rect.Width / 2;
        var radiusY = rect.Height / 2;

        for (var index = 0; index < count; index++)
        {
            var angle = MathF.PI * 2 * index / count;
            points[index] = new SKPoint(
                centerX + radiusX * MathF.Cos(angle),
                centerY + radiusY * MathF.Sin(angle));
        }

        return points;
    }

    private static SKPoint[] SampleRoundedRect(SKRect rect, float radius, int arcSegments)
    {
        var points = new List<SKPoint>();
        var right = rect.Right - radius;
        var bottom = rect.Bottom - radius;
        var left = rect.Left + radius;
        var top = rect.Top + radius;

        // 上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角
        points.Add(new SKPoint(left, rect.Top));
        points.Add(new SKPoint(right, rect.Top));
        AddArc(points, right, top, radius, -MathF.PI / 2, 0, arcSegments);
        points.Add(new SKPoint(rect.Right, bottom));
        AddArc(points, right, bottom, radius, 0, MathF.PI / 2, arcSegments);
        points.Add(new SKPoint(left, rect.Bottom));
        AddArc(points, left, bottom, radius, MathF.PI / 2, MathF.PI, arcSegments);
        points.Add(new SKPoint(rect.Left, top));
        AddArc(points, left, top, radius, MathF.PI, MathF.PI * 1.5f, arcSegments);

        return points.ToArray();
    }

    private static void AddArc(List<SKPoint> points, float centerX, float centerY, float radius,
        float from, float to, int segments)
    {
        for (var index = 1; index <= segments; index++)
        {
            var angle = from + (to - from) * index / segments;
            points.Add(new SKPoint(
                centerX + radius * MathF.Cos(angle),
                centerY + radius * MathF.Sin(angle)));
        }
    }

    /// <summary>一条直线段的描边几何。</summary>
    private static StrokeGeometry SegmentStrokeGeometry(SKPoint from, SKPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return new StrokeGeometry([new[] { from, to }], false, length);
    }

    /// <summary>折线的描边几何：振幅按包围盒短边算。</summary>
    private static StrokeGeometry PolylineStrokeGeometry(SKPoint[] points) =>
        new StrokeGeometry([points], false, ShortSide(points));

    private static StrokeGeometry PenStrokeGeometry(IReadOnlyList<Point> points)
    {
        var vertices = new SKPoint[points.Count];
        for (var index = 0; index < points.Count; index++)
            vertices[index] = new SKPoint((float)points[index].X, (float)points[index].Y);

        return new StrokeGeometry([vertices], false, ShortSide(vertices));
    }

    /// <summary>顶点集包围盒的短边；手绘振幅按它算，避免小图形被抖变形。</summary>
    private static float ShortSide(SKPoint[] points)
    {
        if (points.Length == 0)
            return 0;

        var minX = points[0].X;
        var maxX = points[0].X;
        var minY = points[0].Y;
        var maxY = points[0].Y;

        for (var index = 1; index < points.Length; index++)
        {
            minX = MathF.Min(minX, points[index].X);
            maxX = MathF.Max(maxX, points[index].X);
            minY = MathF.Min(minY, points[index].Y);
            maxY = MathF.Max(maxY, points[index].Y);
        }

        return MathF.Min(maxX - minX, maxY - minY);
    }

    /// <summary>确定性随机：同一个 seed/index 永远得到同一个值，预览和导出保持一致。</summary>
    private static float Sample(uint seed, int index)
    {
        var value = seed + 0x9e3779b9u * (uint)(index + 1);
        value = (value ^ (value >> 16)) * 0x85ebca6bu;
        value = (value ^ (value >> 13)) * 0xc2b2ae35u;
        value ^= value >> 16;
        return value / 2147483647.5f - 1f;
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
        var text = NumberText.Format(number.Number, number.Format, number.Label);

        // 文本要塞进徽章：按 0.55 字号起算，超宽（罗马/中文字标更长）再等比缩小
        var fontSize = number.Size * 0.55;
        double textWidth;

        using (var probe = AnnotationText.CreateFont(fontSize))
            textWidth = probe.MeasureText(text);

        var bounds = NumberGeometry.BadgeBounds(number.Center, number.Size, number.Shape, textWidth);

        var limit = Math.Min(bounds.Width, bounds.Height) * 0.8;
        if (textWidth > limit && textWidth > 0)
        {
            fontSize *= limit / textWidth;
            using var fitted = AnnotationText.CreateFont(fontSize);
            textWidth = fitted.MeasureText(text);
            bounds = NumberGeometry.BadgeBounds(number.Center, number.Size, number.Shape, textWidth);
        }

        var color = new SKColor(number.Color);
        DrawBadge(canvas, bounds, number.Shape, color, paint);

        using var font = AnnotationText.CreateFont(fontSize);
        var anchor = NumberGeometry.TextAnchor(bounds, number.Shape);

        // 描边圆没有底色，文字用同色才看得见
        paint.Style = SKPaintStyle.Fill;
        paint.Color = number.Shape == NumberShape.OutlineCircle ? color : SKColors.White;
        canvas.DrawText(
            text,
            (float)anchor.X,
            (float)anchor.Y + (float)(fontSize * 0.35),
            SKTextAlign.Center,
            font,
            paint);
    }

    /// <summary>序号徽章：多边形族走顶点路径，圆/方族按形状选填充或描边。</summary>
    private static void DrawBadge(SKCanvas canvas, Rect bounds, NumberShape shape, SKColor color, SKPaint paint)
    {
        var x = (float)bounds.X;
        var y = (float)bounds.Y;
        var width = (float)bounds.Width;
        var height = (float)bounds.Height;
        var radius = MathF.Min(width, height) / 2;
        var rect = new SKRect(x, y, x + width, y + height);

        var polygon = NumberGeometry.Polygon(shape, bounds);
        if (polygon is { Count: >= 3 })
        {
            using var path = new SKPath();
            path.MoveTo((float)polygon[0].X, (float)polygon[0].Y);

            for (var index = 1; index < polygon.Count; index++)
                path.LineTo((float)polygon[index].X, (float)polygon[index].Y);

            path.Close();

            paint.Style = SKPaintStyle.Fill;
            paint.Color = color;
            canvas.DrawPath(path, paint);
            return;
        }

        paint.Color = color;

        switch (shape)
        {
            case NumberShape.OutlineCircle:
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = MathF.Max(1, radius * 0.1f);
                canvas.DrawOval(rect, paint);
                break;

            case NumberShape.DoubleCircle:
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = MathF.Max(1, radius * 0.065f);
                canvas.DrawOval(rect, paint);

                var inset = radius * 0.2f;
                var inner = new SKRect(x + inset, y + inset, x + width - inset, y + height - inset);
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawOval(inner, paint);
                break;
            }

            case NumberShape.SolidSquare:
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(rect, paint);
                break;

            case NumberShape.RoundedSquare:
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRoundRect(rect, radius * 0.3f, radius * 0.3f, paint);
                break;

            case NumberShape.Capsule:
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRoundRect(rect, radius, radius, paint);
                break;

            case NumberShape.Pin:
            {
                var (center, headRadius, tip, left, right) = NumberGeometry.Pin(bounds);

                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle((float)center.X, (float)center.Y, (float)headRadius, paint);

                // 圆头 + 切线三角：同色两次填充，视觉上就是一体的 pin
                using var triangle = new SKPath();
                triangle.MoveTo((float)tip.X, (float)tip.Y);
                triangle.LineTo((float)left.X, (float)left.Y);
                triangle.LineTo((float)right.X, (float)right.Y);
                triangle.Close();
                canvas.DrawPath(triangle, paint);
                break;
            }

            default:
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawOval(rect, paint);
                break;
        }
    }

    /// <summary>文字：背景 → 描边 → 正文；对齐切换锚点，Origin 始终是左基准点。</summary>
    private static void DrawTextAnnotation(
        SKCanvas canvas, TextAnnotation text, double width, SKPaint paint)
    {
        paint.PathEffect = null;

        var anchorX = text.Origin.X + (text.Align switch
        {
            TextAlign.Center => width / 2,
            TextAlign.Right => width,
            _ => 0
        });

        var align = text.Align switch
        {
            TextAlign.Center => SKTextAlign.Center,
            TextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Left
        };

        if (text.BackgroundColor != 0)
        {
            var background = new SKRect(
                (float)(text.Origin.X - AnnotationText.PaddingX),
                (float)(text.Origin.Y - text.FontSize - AnnotationText.PaddingY),
                (float)(text.Origin.X + width + AnnotationText.PaddingX),
                (float)(text.Origin.Y + text.FontSize * 0.25 + AnnotationText.PaddingY));

            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(text.BackgroundColor);
            canvas.DrawRoundRect(background, 3, 3, paint);
        }

        using var font = AnnotationText.CreateFont(
            text.FontSize, text.Bold, text.FontFamily, text.Italic);

        if (text.StrokeWidth > 0 && text.StrokeColor != 0)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)text.StrokeWidth;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.Color = new SKColor(text.StrokeColor);
            canvas.DrawText(text.Text, (float)anchorX, (float)text.Origin.Y, align, font, paint);
        }

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(text.Color);
        canvas.DrawText(text.Text, (float)anchorX, (float)text.Origin.Y, align, font, paint);
    }

    /// <summary>可选旋转：以 center 为轴执行一次绘制，不影响后续图元。</summary>
    private static void WithRotation(SKCanvas canvas, Point center, double rotation, Action draw)
    {
        if (Math.Abs(rotation) < 0.01)
        {
            draw();
            return;
        }

        canvas.Save();
        canvas.RotateDegrees((float)rotation, (float)center.X, (float)center.Y);
        draw();
        canvas.Restore();
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
