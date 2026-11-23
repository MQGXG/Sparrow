using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

/// <summary>OCR 文字标记的四种类型（和 LumaShot 的 TextDecoration 对齐）。</summary>
internal enum TextMarkKind
{
    Highlight,
    Wave,
    Underline,
    Strike
}

/// <summary>
/// 一条文字标记：类型 + 一组框（选区内 DIP 坐标）。
/// 框来自字符选择，所以一条标记可能跨多个字框或整行。
/// </summary>
internal sealed record OcrTextMark(
    TextMarkKind Kind,
    IReadOnlyList<Rect> Boxes,
    uint Color,
    double Width);

internal static class OcrTextMarks
{
    /// <summary>荧光笔：琥珀 35%，和标注色一致，深色截图上也看得见。</summary>
    private const uint HighlightColor = 0x59E5A44C;

    /// <summary>波浪线：红，和 LumaShot 的 #F43F67 同族。</summary>
    private const uint WaveColor = 0xFFFF5F5F;

    /// <summary>下划线 / 删除线：琥珀，避免用深色在暗背景上消失。</summary>
    private const uint LineColor = 0xFFE5A44C;

    public static uint ColorFor(TextMarkKind kind) => kind switch
    {
        TextMarkKind.Highlight => HighlightColor,
        TextMarkKind.Wave => WaveColor,
        _ => LineColor
    };

    public static string Label(TextMarkKind kind) => kind switch
    {
        TextMarkKind.Highlight => "荧光笔",
        TextMarkKind.Wave => "波浪线",
        TextMarkKind.Underline => "下划线",
        _ => "删除线"
    };

    /// <summary>
    /// 画到 Skia 画布。坐标是 DIP，画布已经按缩放系数缩放过，
    /// 所以这里不需要再乘 scale。
    /// </summary>
    public static void Draw(SKCanvas canvas, IEnumerable<OcrTextMark> marks)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        foreach (var mark in marks)
        {
            var color = new SKColor(mark.Color);

            foreach (var box in mark.Boxes)
            {
                if (box.Width <= 0 || box.Height <= 0)
                    continue;

                var rect = new SKRect(
                    (float)box.X, (float)box.Y, (float)box.Right, (float)box.Bottom);

                if (mark.Kind == TextMarkKind.Highlight)
                {
                    fill.Color = color;
                    canvas.DrawRect(rect, fill);
                    continue;
                }

                // 线宽跟着行高走，和 LumaShot 的 clamp(行高/14, 1.5, 3) 一致
                var width = (float)Math.Clamp(mark.Width > 0 ? mark.Width : box.Height / 14, 1.5, 3);
                stroke.Color = color;
                stroke.StrokeWidth = width;

                var y = mark.Kind == TextMarkKind.Strike
                    ? (float)box.Center.Y
                    : (float)box.Bottom - 1;

                using var path = new SKPath();
                if (mark.Kind == TextMarkKind.Wave)
                {
                    // 正弦折线：振幅等于线宽，波长约 9 DIP
                    path.MoveTo((float)box.X, y);
                    for (var x = box.X + 1; x <= box.Right; x += 1)
                        path.LineTo((float)x, y + (float)(Math.Sin((x - box.X) * 0.7) * width));
                }
                else
                {
                    path.MoveTo((float)box.X, y);
                    path.LineTo((float)box.Right, y);
                }

                canvas.DrawPath(path, stroke);
            }
        }
    }
}
