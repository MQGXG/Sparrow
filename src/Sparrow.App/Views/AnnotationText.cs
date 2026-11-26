using Avalonia;
using SkiaSharp;
using System.Collections.Concurrent;

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

    private static IReadOnlyList<string>? _families;

    /// <summary>
    /// 字体族解析要走系统字体管理器，按「族名 + 粗体 + 斜体」缓存，
    /// 避免每次测量/导出都重新查找一遍字体。
    /// </summary>
    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SKTypeface>
        TypefaceCache = new();

    /// <summary>系统字体族：用 Skia 枚举，保证预览和导出看到同一份列表。</summary>
    public static IReadOnlyList<string> Families =>
        _families ??= SKFontManager.Default.FontFamilies
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static SKFont CreateFont(double size, bool bold = false, string? family = null, bool italic = false) =>
        new(ResolveTypeface(family, bold, italic), (float)Math.Max(1, size));

    private static SKTypeface ResolveTypeface(string? family, bool bold, bool italic)
    {
        var key = (family ?? string.Empty, bold, italic);

        return TypefaceCache.GetOrAdd(key, static requested =>
        {
            var style = new SKFontStyle(
                requested.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                requested.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

            var typeface =
                (!string.IsNullOrWhiteSpace(requested.Family)
                    ? SKTypeface.FromFamilyName(requested.Family, style)
                    : null)
                ?? SKTypeface.FromFamilyName(Family, style)
                ?? SKTypeface.Default;

            return typeface;
        });
    }

    /// <summary>导出的文字宽度（DIP）。预览侧用 TextBlock 实测，不调用这里。</summary>
    public static double Measure(string text, double size, bool bold = false,
        string? family = null, bool italic = false)
    {
        using var font = CreateFont(size, bold, family, italic);
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

            // 逐字符测量时用 char 缓冲 + span 重载，避免每个字符都 ToString 一次
            var buffer = new char[paragraph.Length];
            var length = 0;

            foreach (var ch in paragraph)
            {
                buffer[length++] = ch;

                if (length == 1 || font.MeasureText(buffer.AsSpan(0, length)) <= widthLimit)
                    continue;

                var breakAt = buffer.AsSpan(0, length).LastIndexOf(' ');

                if (breakAt > 0)
                {
                    lines.Add(new string(buffer, 0, breakAt));

                    var remaining = length - breakAt - 1;
                    if (remaining > 0)
                        Array.Copy(buffer, breakAt + 1, buffer, 0, remaining);

                    length = remaining;
                    continue;
                }

                length--;
                if (length > 0)
                    lines.Add(new string(buffer, 0, length));

                buffer[0] = ch;
                length = 1;
            }

            if (length > 0)
                lines.Add(new string(buffer, 0, length));
        }

        return lines;
    }
}
