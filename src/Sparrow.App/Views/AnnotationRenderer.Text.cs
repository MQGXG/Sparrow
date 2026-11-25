// AnnotationRenderer 分部类：水印、文字标注、旋转与透明度工具。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
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
}
