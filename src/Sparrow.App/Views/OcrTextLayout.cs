using System.Text;
using Avalonia;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.Views;

/// <summary>
/// OCR 行的字符级近似排版。
///
/// Paddle 的检测只给「文本行」的框（`OcrTextLine` 只有 Text/X/Y/W/H），
/// 拿不到 LumaShot 那种逐字框（它用 CTC 解码直接出 glyph box）。
/// 所以在宿主侧按字符类型分配宽度：同一行的字宽之和正好等于行宽，
/// 这样拖选时手柄能落在字与字之间，误差不超过一个字。
/// </summary>
internal static class OcrTextLayout
{
    /// <summary>字符在行内的框；坐标与传入的行一致（DIP 或物理像素）。</summary>
    internal readonly record struct OcrCharBox(int Index, string Text, Rect Box);

    /// <summary>把一行拆成字符框。空文本或非法框返回空。</summary>
    internal static IReadOnlyList<OcrCharBox> Split(OcrTextLine line)
    {
        if (string.IsNullOrEmpty(line.Text) || line.Width <= 0 || line.Height <= 0)
            return Array.Empty<OcrCharBox>();

        var units = new List<(int Index, string Text, double Weight)>(line.Text.Length);
        var index = 0;

        // 按字素切：代理对（emoji 等）算一个字符，避免选中半个
        foreach (var rune in line.Text.EnumerateRunes())
        {
            units.Add((index++, rune.ToString(), Weight(rune)));
        }

        var total = 0.0;
        foreach (var unit in units)
            total += unit.Weight;

        if (units.Count == 0 || total <= 0)
            return Array.Empty<OcrCharBox>();

        var scale = line.Width / total;
        var result = new List<OcrCharBox>(units.Count);
        var x = line.X;

        for (var i = 0; i < units.Count; i++)
        {
            var (unitIndex, text, weight) = units[i];
            var width = weight * scale;

            // 最后一个字符收到行右边界，消掉浮点累积误差
            if (i == units.Count - 1)
                width = Math.Max(0, line.X + line.Width - x);

            result.Add(new OcrCharBox(unitIndex, text, new Rect(x, line.Y, width, line.Height)));
            x += width;
        }

        return result;
    }

    /// <summary>点落在第几个字符上；返回 -1 表示不在行框内。</summary>
    internal static int HitIndex(IReadOnlyList<OcrCharBox> boxes, Point point)
    {
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Box.Contains(point))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 点 → 字符边界下标（0..Count）。落在字上返回该字下标；
    /// 落在行左/右外侧时钳到 0 / Count，这样拖选能拖到行首行尾。
    /// </summary>
    internal static int IndexAt(IReadOnlyList<OcrCharBox> boxes, Point point)
    {
        if (boxes.Count == 0)
            return -1;

        var hit = HitIndex(boxes, point);
        if (hit >= 0)
            return hit;

        if (point.X <= boxes[0].Box.X)
            return 0;

        if (point.X >= boxes[^1].Box.Right)
            return boxes.Count;

        for (var i = 0; i < boxes.Count; i++)
        {
            if (point.X < boxes[i].Box.X)
                return i;
        }

        return boxes.Count;
    }

    /// <summary>取 [start, start+length) 的字符框，按连续段合并成一个包围盒。</summary>
    internal static IReadOnlyList<Rect> BoxesForRange(
        IReadOnlyList<OcrCharBox> boxes, int start, int length)
    {
        if (length <= 0 || boxes.Count == 0)
            return Array.Empty<Rect>();

        start = Math.Clamp(start, 0, boxes.Count);
        var end = Math.Clamp(start + length, start, boxes.Count);
        if (start >= end)
            return Array.Empty<Rect>();

        var first = boxes[start].Box;
        var left = first.X;
        var top = first.Y;
        var right = first.Right;
        var bottom = first.Bottom;

        for (var i = start + 1; i < end; i++)
        {
            var box = boxes[i].Box;
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }

        return [new Rect(left, top, right - left, bottom - top)];
    }

    /// <summary>取 [start, start+length) 的原文。</summary>
    internal static string TextForRange(string text, int start, int length)
    {
        if (string.IsNullOrEmpty(text) || length <= 0)
            return string.Empty;

        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + length, start, text.Length);
        return text[start..end];
    }

    /// <summary>
    /// 字符宽度权重。中日韩/全角算 1，西文约 0.55，空格 0.3，其余 ASCII 约 0.35。
    /// 数值来自常见 UI 字体的经验比例，不追求排版精度，只要求「同一行内相对正确」。
    /// </summary>
    internal static double Weight(Rune rune)
    {
        var value = rune.Value;

        if (value == ' ' || value == '\t')
            return 0.3;

        // CJK 统一表意文字、假名、韩文、全角形式、CJK 标点
        if (value >= 0x2E80)
            return 1.0;

        if (value < 0x80)
            return char.IsAsciiLetterOrDigit((char)value) ? 0.55 : 0.35;

        // 拉丁扩展、西里尔、希腊等
        return 0.6;
    }
}
