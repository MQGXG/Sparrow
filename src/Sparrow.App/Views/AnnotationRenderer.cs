using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

/// <summary>
/// 把标注合成进 PNG。
///
/// 用 Skia 而不是 Avalonia 的渲染截图：图元是 DIP 坐标，导出时按缩放乘上去，
/// 高 DPI 下线条不会发虚。项目本来就依赖 SkiaSharp（OCR、SVG 也在用）。
/// </summary>
internal static partial class AnnotationRenderer
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
                DrawNumber(canvas, number, paint, source);
                break;
        }
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
