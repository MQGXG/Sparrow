// AnnotationRenderer 分部类：直线、测距、角度、括号、气泡与箭头。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
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
}
