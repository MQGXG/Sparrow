// AnnotationRenderer 分部类：形状轮廓与填充（矩形/圆角/椭圆/菱形、斜线填充）。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
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
}
