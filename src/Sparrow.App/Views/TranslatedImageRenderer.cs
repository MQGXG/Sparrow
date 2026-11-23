using System.Text;
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

    /// <summary>左右对比图：左原文、右译文，中间留一条缝，深色底。</summary>
    public static byte[]? BuildCompare(SKBitmap original, SKBitmap translated, int gap = 16)
    {
        if (original.Width <= 0 || translated.Width <= 0 || original.Height <= 0)
            return null;

        var width = original.Width + gap + translated.Width;
        var height = Math.Max(original.Height, translated.Height);

        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x14, 0x18, 0x1D));
            canvas.DrawBitmap(original, 0, 0);
            canvas.DrawBitmap(translated, original.Width + gap, 0);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>行距系数：和 snow-shot 一致，略大于字号。</summary>
    private const float LineAdvance = 1.28f;

    /// <summary>最小字号：再小就看不清，宁可按框裁剪。</summary>
    private const float MinFontSize = 6f;

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
        var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.Default;

        // 内边距按行高给；格子很窄时按宽度再收一点，避免留白吃掉字宽
        var padding = Math.Min(rect.Height * 0.08f, rect.Width * 0.04f);
        var maxWidth = Math.Max(1f, rect.Width - padding * 2);
        var maxHeight = Math.Max(1f, rect.Height - padding * 2);

        // 字号上限取行框短边的 82%（snow-shot 的 fontSizeCapRatio），再按折行结果往下缩
        var size = Math.Max(MinFontSize, Math.Min(rect.Height, rect.Width) * 0.82f);
        List<string> rows;

        using var font = new SKFont(typeface, size);
        while (true)
        {
            font.Size = size;
            rows = WrapText(text, font, maxWidth);

            // 放得下就停；到最小字号还放不下就不再缩，交给 ClipRect 裁剪（不越出格子）
            if (rows.Count * size * LineAdvance <= maxHeight || size <= MinFontSize)
                break;

            size = Math.Max(MinFontSize, size * 0.9f);
        }

        using (var paint = new SKPaint { IsAntialias = true, Color = ink })
        {
            canvas.Save();
            canvas.ClipRect(rect);

            // 左对齐 + 垂直居中：和 snow-shot 的单行规则一致
            var blockHeight = rows.Count * size * LineAdvance;
            var baseline = rect.MidY - blockHeight / 2 + size * 0.8f;

            foreach (var row in rows)
            {
                canvas.DrawText(row, rect.Left + padding, baseline, SKTextAlign.Left, font, paint);
                baseline += size * LineAdvance;
            }

            canvas.Restore();
        }
    }

    /// <summary>按框宽折行：先尊重原文换行；英文优先在空格处断，中文按字符断。</summary>
    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var rows = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var current = new StringBuilder();

            foreach (var ch in paragraph)
            {
                current.Append(ch);

                if (font.MeasureText(current.ToString()) <= maxWidth)
                    continue;

                var pending = current.ToString();
                var breakAt = pending.LastIndexOf(' ');

                if (breakAt > 0)
                {
                    // 现在空格处断：英文单词不被拆成两半
                    rows.Add(pending[..breakAt]);
                    current.Clear();
                    current.Append(pending[(breakAt + 1)..]);
                    continue;
                }

                // 没有空格可断（中文或超长单词）：退回一个字符
                current.Length--;
                if (current.Length > 0)
                    rows.Add(current.ToString());

                current.Clear();
                current.Append(ch);
            }

            if (current.Length > 0)
                rows.Add(current.ToString());
        }

        return rows;
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
