// AnnotationRenderer 分部类：序号徽章、外形与组合（引导线/箭头/文本/虚线框/高亮/放大）。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
    private static void DrawNumber(SKCanvas canvas, NumberAnnotation number, SKPaint paint, SKBitmap source)
    {
        var text = NumberText.Format(number.Number, number.Format, number.Label);
        var hasLabel = !string.IsNullOrWhiteSpace(number.Label);

        // 文本要塞进徽章：按 0.55 字号起算，超宽（罗马/中文字标更长）再等比缩小
        var fontSize = number.Size * 0.55;
        double textWidth;

        using (var probe = AnnotationText.CreateFont(fontSize))
            textWidth = probe.MeasureText(text);

        // 只有自定义标签才按文字撑宽；自动数字保持正圆/正方
        var bounds = NumberGeometry.BadgeBounds(
            number.Center, number.Size, number.Shape, hasLabel ? textWidth : 0);

        var limit = Math.Min(bounds.Width, bounds.Height) * 0.8;
        if (textWidth > limit && textWidth > 0)
        {
            fontSize *= limit / textWidth;

            if (hasLabel)
            {
                using var fitted = AnnotationText.CreateFont(fontSize);
                textWidth = fitted.MeasureText(text);
                bounds = NumberGeometry.BadgeBounds(
                    number.Center, number.Size, number.Shape, textWidth);
            }
        }

        var color = new SKColor(number.Color);
        DrawNumberDetail(canvas, number, paint, source);
        DrawBadge(canvas, bounds, number.Shape, color, paint);

        using var font = AnnotationText.CreateFont(fontSize);
        var anchor = NumberGeometry.TextAnchor(bounds, number.Shape);

        // 描边圆没有底色，文字用同色才看得见
        paint.Style = SKPaintStyle.Fill;
        paint.Color = number.Shape == NumberShape.OutlineCircle ? color : SKColors.White;
        // 用字体大写高度做垂直居中：数字/字母都落在这个高度内
        var baseline = (float)anchor.Y - font.Metrics.CapHeight / 2;
        canvas.DrawText(
            text,
            (float)anchor.X,
            baseline,
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

    /// <summary>组合详情区：引导线 / 箭头 / 文本标签 / 虚线框 / 高亮区域 / 放大说明。</summary>
    private static void DrawNumberDetail(SKCanvas canvas, NumberAnnotation number, SKPaint paint, SKBitmap source)
    {
        switch (number.Combo)
        {
            case NumberCombo.Leader:
                DrawNumberLeader(canvas, number, paint);
                break;

            case NumberCombo.Arrow:
                DrawNumberArrow(canvas, number, paint);
                break;

            case NumberCombo.Text:
                DrawNumberTextDetail(canvas, number, paint);
                break;

            case NumberCombo.DashedBox:
            case NumberCombo.Highlight:
                DrawNumberBoxDetail(canvas, number, paint);
                break;

            case NumberCombo.Magnify:
                DrawNumberMagnify(canvas, number, paint, source);
                break;
        }
    }

    /// <summary>文本标签：badge 旁一块说明文字，底色跟序号色，空文本显示占位。</summary>
    private static void DrawNumberTextDetail(SKCanvas canvas, NumberAnnotation number, SKPaint paint)
    {
        var detail = NumberGeometry.DetailBounds(number);
        if (detail.Width < 4 || detail.Height < 4)
            return;

        var color = new SKColor(number.Color);
        var radius = (float)Math.Min(number.Size * 0.2, Math.Min(detail.Width, detail.Height) / 2);
        var rect = new SKRect(
            (float)detail.X, (float)detail.Y, (float)detail.Right, (float)detail.Bottom);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = color.WithAlpha(230);
        canvas.DrawRoundRect(rect, radius, radius, paint);

        var placeholder = string.IsNullOrWhiteSpace(number.Text);
        var text = placeholder ? "说明" : number.Text!;
        var fontSize = number.TextSize;

        using (var probe = AnnotationText.CreateFont(fontSize))
        {
            var limit = Math.Max(8, detail.Width - number.Size * 0.6);
            var measured = probe.MeasureText(text);

            if (measured > limit && measured > 0)
                fontSize *= limit / measured;
        }

        using var font = AnnotationText.CreateFont(fontSize);
        paint.Color = placeholder ? SKColors.White.WithAlpha(170) : SKColors.White;
        canvas.DrawText(
            text,
            rect.MidX,
            rect.MidY + (float)(fontSize * 0.35),
            SKTextAlign.Center,
            font,
            paint);
    }

    /// <summary>放大说明：badge 连到目标点处的圆形镜片，镜片里显示放大后的原图。</summary>
    private static void DrawNumberMagnify(SKCanvas canvas, NumberAnnotation number, SKPaint paint, SKBitmap source)
    {
        var detail = NumberGeometry.DetailBounds(number);
        if (detail.Width < 6 || detail.Height < 6)
            return;

        var badge = NumberGeometry.Bounds(number);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        paint.Color = new SKColor(number.Color);
        canvas.DrawLine(
            (float)badge.Center.X, (float)badge.Center.Y,
            (float)detail.Center.X, (float)detail.Center.Y,
            paint);

        var lens = new MagnifierAnnotation(detail, Zoom: number.Zoom, Circle: true, BorderColor: number.Color);
        DrawMagnifier(canvas, lens, paint, source);
    }

    /// <summary>引导线：4 点虚线折线 + 目标端圆点。</summary>
    private static void DrawNumberLeader(SKCanvas canvas, NumberAnnotation number, SKPaint paint)
    {
        var points = NumberGeometry.LeaderPoints(number);
        var radius = number.Size * 0.24;

        using var path = new SKPath();
        path.MoveTo((float)points[0].X, (float)points[0].Y);

        // 肘点圆角：两段各缩进 cut，再用二次贝塞尔绕过拐角
        for (var index = 1; index < points.Count - 1; index++)
        {
            var prev = points[index - 1];
            var corner = points[index];
            var next = points[index + 1];

            var inLength = Math.Sqrt(Math.Pow(corner.X - prev.X, 2) + Math.Pow(corner.Y - prev.Y, 2));
            var outLength = Math.Sqrt(Math.Pow(next.X - corner.X, 2) + Math.Pow(next.Y - corner.Y, 2));

            if (inLength < 0.001 || outLength < 0.001)
            {
                path.LineTo((float)corner.X, (float)corner.Y);
                continue;
            }

            var cut = Math.Min(radius, Math.Min(inLength, outLength) * 0.5);

            path.LineTo(new SKPoint(
                (float)(corner.X + (prev.X - corner.X) * cut / inLength),
                (float)(corner.Y + (prev.Y - corner.Y) * cut / inLength)));

            path.QuadTo(
                new SKPoint((float)corner.X, (float)corner.Y),
                new SKPoint(
                    (float)(corner.X + (next.X - corner.X) * cut / outLength),
                    (float)(corner.Y + (next.Y - corner.Y) * cut / outLength)));
        }

        path.LineTo((float)points[^1].X, (float)points[^1].Y);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        paint.Color = new SKColor(number.Color);
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        paint.PathEffect = SKPathEffect.CreateDash([6, 4], 0);
        canvas.DrawPath(path, paint);
        paint.PathEffect = null;

        paint.Style = SKPaintStyle.Fill;
        canvas.DrawCircle((float)points[^1].X, (float)points[^1].Y, 3, paint);

        paint.StrokeCap = SKStrokeCap.Butt;
        paint.StrokeJoin = SKStrokeJoin.Miter;
    }

    /// <summary>箭头：从 badge 边缘指向目标，复用箭头渲染。</summary>
    private static void DrawNumberArrow(SKCanvas canvas, NumberAnnotation number, SKPaint paint)
    {
        var badge = NumberGeometry.Bounds(number);
        var center = badge.Center;
        var target = NumberGeometry.TargetOf(number);
        var dx = target.X - center.X;
        var dy = target.Y - center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < number.Size * 0.5)
            return;

        var start = new Point(
            center.X + dx / length * (badge.Width / 2 + 2),
            center.Y + dy / length * (badge.Height / 2 + 2));

        DrawArrow(canvas, new ArrowAnnotation(start, target, number.Color, 2), paint);
    }

    /// <summary>虚线框 / 高亮区域：浅底，虚线框多一圈虚线描边。</summary>
    private static void DrawNumberBoxDetail(SKCanvas canvas, NumberAnnotation number, SKPaint paint)
    {
        var detail = NumberGeometry.DetailBounds(number);
        if (detail.Width < 4 || detail.Height < 4)
            return;

        var color = new SKColor(number.Color);
        var dashed = number.Combo == NumberCombo.DashedBox;
        var radius = (float)Math.Min(number.Size * 0.2, Math.Min(detail.Width, detail.Height) / 2);
        var rect = new SKRect(
            (float)detail.X, (float)detail.Y, (float)detail.Right, (float)detail.Bottom);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = color.WithAlpha(dashed ? (byte)20 : (byte)46);   // 8% / 18%
        canvas.DrawRoundRect(rect, radius, radius, paint);

        if (!dashed)
            return;

        paint.Style = SKPaintStyle.Stroke;
        paint.Color = color.WithAlpha(150);
        paint.StrokeWidth = 1;
        paint.PathEffect = SKPathEffect.CreateDash([6, 4], 0);
        canvas.DrawRoundRect(rect, radius, radius, paint);
        paint.PathEffect = null;
    }
}
