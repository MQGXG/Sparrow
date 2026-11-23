using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

/// <summary>
/// 把译文画成一张「译文图」：按行盖掉原文、写译文。
///
/// snow-shot 的做法是逐行采样原图底色再写字，这样译文图和原图在视觉上是同一张表；
/// 这里照做——底色取行框四角的平均色，文字颜色按底色亮度反选。
/// </summary>
internal static class TranslatedImageRenderer
{
    /// <summary>一行：选区内的 DIP 框 + 原文 + 译文。</summary>
    internal readonly record struct LineBox(Rect Box, string Source, string? Translation);

    /// <summary>返回译文图 PNG；一行可用译文都没有时返回 null。</summary>
    public static byte[]? Render(SKBitmap crop, double scale, IReadOnlyList<LineBox> lines)
    {
        if (crop.Width <= 0 || crop.Height <= 0)
            return null;

        var usable = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Translation) && line.Box.Width > 2)
            .ToList();

        if (usable.Count == 0)
            return null;

        using var bitmap = new SKBitmap(crop.Width, crop.Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawBitmap(crop, 0, 0);
            canvas.Scale((float)Math.Max(scale, 0.01));

            foreach (var line in usable)
                DrawLine(canvas, crop, line, scale);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawLine(SKCanvas canvas, SKBitmap crop, LineBox line, double scale)
    {
        var rect = new SKRect(
            (float)line.Box.X,
            (float)line.Box.Y,
            (float)line.Box.Right,
            (float)line.Box.Bottom);

        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var background = SampleBackground(crop, line.Box, scale);
        var ink = Luminance(background) > 0.55 ? SKColors.Black : SKColors.White;

        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = background })
            canvas.DrawRect(rect, fill);

        var text = line.Translation!;
        var (font, width) = FitFont(text, rect.Width, rect.Height);

        using (font)
        using (var paint = new SKPaint { IsAntialias = true, Color = ink })
        {
            // 缩到最小字号还放不下时，宁可裁掉也不越出格子
            canvas.Save();
            canvas.ClipRect(rect);

            var x = rect.Left + Math.Max(0, (rect.Width - width) / 2);
            var baseline = rect.MidY + font.Size * 0.36f;
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);

            canvas.Restore();
        }
    }

    /// <summary>从大到小缩字号，直到宽度和高度都放得下。</summary>
    private static (SKFont Font, float Width) FitFont(string text, float maxWidth, float maxHeight)
    {
        var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.Default;
        var size = Math.Max(6, maxHeight * 0.82f);

        while (true)
        {
            var font = new SKFont(typeface, size);
            var width = font.MeasureText(text);

            if ((width <= maxWidth - 2 && size <= maxHeight - 2) || size <= 6)
                return (font, width);

            font.Dispose();
            size *= 0.88f;
        }
    }

    /// <summary>取行框四角外侧一点的平均色，作为盖住原文的底色。</summary>
    private static SKColor SampleBackground(SKBitmap crop, Rect box, double scale)
    {
        var pad = (float)Math.Max(1, box.Height * 0.12);

        var points = new (float X, float Y)[]
        {
            ((float)box.X - pad, (float)box.Y - pad),
            ((float)box.Right + pad, (float)box.Y - pad),
            ((float)box.X - pad, (float)box.Bottom + pad),
            ((float)box.Right + pad, (float)box.Bottom + pad)
        };

        var sumR = 0;
        var sumG = 0;
        var sumB = 0;
        var count = 0;

        foreach (var (x, y) in points)
        {
            var px = (int)Math.Clamp(x * scale, 0, crop.Width - 1);
            var py = (int)Math.Clamp(y * scale, 0, crop.Height - 1);
            var color = crop.GetPixel(px, py);
            sumR += color.Red;
            sumG += color.Green;
            sumB += color.Blue;
            count++;
        }

        return count == 0
            ? SKColors.White
            : new SKColor((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
    }

    private static double Luminance(SKColor color) =>
        (0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue) / 255.0;
}
