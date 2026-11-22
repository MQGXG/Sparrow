using Avalonia;
using SkiaSharp;
using System.Text;

namespace Sparrow.App.Views;

/// <summary>
/// 预览和导出共用的文字度量。
///
/// 预览用 TextBlock、导出用 SKFont，两边字体族和字号必须一致；
/// 雅黑在两种渲染器下会有 1–2px 的宽度差，所以带底的标签统一留 4/2 内边距，
/// 不能把文字贴到边框上。
/// </summary>
internal static class AnnotationText
{
    public const string Family = "Microsoft YaHei UI";
    public const double PaddingX = 4;
    public const double PaddingY = 2;

    public static SKFont CreateFont(double size, bool bold = false)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var typeface = SKTypeface.FromFamilyName(Family, style) ?? SKTypeface.Default;
        return new SKFont(typeface, (float)Math.Max(1, size));
    }

    /// <summary>导出的文字宽度（DIP）。预览侧用 TextBlock 实测，不调用这里。</summary>
    public static double Measure(string text, double size, bool bold = false)
    {
        using var font = CreateFont(size, bold);
        return font.MeasureText(text);
    }

    /// <summary>以 center 为中心的标签底：宽 = 文字宽 + 4×2，高 = 字号 + 2×2。</summary>
    public static Rect PillRect(Point center, string text, double size, bool bold = false)
    {
        var width = Measure(text, size, bold) + PaddingX * 2;
        var height = size + PaddingY * 2;
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    /// <summary>
    /// 按字符换行（中英文都适用），保留显式换行；
    /// 气泡预览用 TextBlock 自动换行，导出用这里的结果近似对齐。
    /// </summary>
    public static IReadOnlyList<string> Wrap(
        string text, double size, double maxWidth, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        using var font = CreateFont(size, bold);
        var widthLimit = Math.Max(8, maxWidth);
        var lines = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();

            foreach (var ch in paragraph)
            {
                current.Append(ch);

                if (font.MeasureText(current.ToString()) <= widthLimit || current.Length == 1)
                    continue;

                current.Length--;
                lines.Add(current.ToString());
                current.Clear();
                current.Append(ch);
            }

            if (current.Length > 0)
                lines.Add(current.ToString());
        }

        return lines;
    }
}
