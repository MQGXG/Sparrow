using System.Text;
using Avalonia;

namespace Sparrow.App.Views;

/// <summary>截图标注工具。</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Diamond,
    Arrow,
    Line,
    Distance,
    Angle,
    Eraser,
    Spotlight,
    Magnifier,
    Bracket,
    Watermark,
    Bubble,
    Pen,
    Highlighter,
    Mosaic,
    Text,
    Number
}

/// <summary>序号的数字类型；只管文本怎么写，不管徽章形状。</summary>
public enum NumberFormat
{
    /// <summary>阿拉伯数字：1, 2, 3</summary>
    Arabic,

    /// <summary>罗马数字：Ⅰ, Ⅱ, Ⅲ</summary>
    Roman,

    /// <summary>小写字母：a, b, c</summary>
    LowerLetter,

    /// <summary>大写字母：A, B, C</summary>
    UpperLetter,

    /// <summary>中文数字：一, 二, 三</summary>
    Chinese
}

/// <summary>标注线条图案。</summary>
public enum LineDash
{
    Solid,
    Dash,
    Dot
}

/// <summary>描边笔触：同一类线条的三种手写质感。</summary>
public enum LineStyle
{
    /// <summary>规整：边缘干净，像直尺画出来的。</summary>
    Neat,

    /// <summary>手绘：沿路径轻微抖动，接近手画的线。</summary>
    Sketch,

    /// <summary>涂鸦：抖动更明显，并叠一遍双线。</summary>
    Rough
}

/// <summary>形状填充：无 / 淡色（颜色 × 28%） / 实心 / 斜线 / 交叉斜线。</summary>
public enum ShapeFill
{
    None,
    Soft,
    Solid,

    /// <summary>斜线图案（参考 snow-shot 的 Line）。</summary>
    Line,

    /// <summary>交叉斜线图案（参考 snow-shot 的 CrossLine）。</summary>
    CrossLine
}

/// <summary>
/// 一条标注。坐标是选区内相对坐标（DIP），导出时乘缩放系数。
/// 全部做成不可变记录：撤销就是「弹出最后一条再重画」。
/// </summary>
public interface IAnnotation;

public sealed record ShapeAnnotation(
    Rect Rect,
    uint Color,
    double Width,
    bool Ellipse,
    double Opacity = 1,
    ShapeFill Fill = ShapeFill.None,
    double Radius = 0,
    LineDash Dash = LineDash.Solid,
    double Rotation = 0,
    uint FillColor = 0,
    bool Diamond = false,
    LineStyle Style = LineStyle.Neat) : IAnnotation;

public sealed record ArrowAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid,
    LineStyle Style = LineStyle.Neat) : IAnnotation;

/// <summary>直线：只有线身，没有箭头。</summary>
public sealed record LineAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid,
    LineStyle Style = LineStyle.Neat) : IAnnotation;

/// <summary>测距：线 + 两端刻度 + 长度标签；Label 在创建时按当时的单位算好，切单位不回溯。</summary>
public sealed record DistanceAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    string Label,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid,
    LineStyle Style = LineStyle.Neat) : IAnnotation;

/// <summary>角度：顶点 + 两条边 + 0–180° 标签；Label 在提交时定稿。</summary>
public sealed record AngleAnnotation(
    Point Vertex,
    Point A,
    Point B,
    uint Color,
    double Width,
    string Label,
    double Opacity = 1) : IAnnotation;

/// <summary>括号：竖边 + 两条横臂；OpenRight = 开口朝右（`[`）。</summary>
public sealed record BracketAnnotation(
    Rect Rect,
    bool OpenRight,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid,
    double Rotation = 0,
    LineStyle Style = LineStyle.Neat) : IAnnotation
{
    /// <summary>横臂长度：不超过 24，也不超过短边的一半。</summary>
    public double Depth => Math.Min(Math.Min(24, Rect.Height / 3), Rect.Width / 2);
}

/// <summary>聚光灯：整幅压暗，只在 Rect 处挖洞。Opacity 是“暗度”。</summary>
public sealed record SpotlightAnnotation(
    Rect Rect,
    uint Color,
    double Opacity = 0.55,
    bool Ellipse = true) : IAnnotation;

/// <summary>放大镜：把 Rect 处的画面按 Zoom 倍放大，原地铺开（尺寸也跟着变大）。</summary>
public sealed record MagnifierAnnotation(
    Rect Rect,
    double Zoom = 2.5,
    bool Circle = false,
    uint BorderColor = 0xFFE5A44C) : IAnnotation;

/// <summary>对话气泡：圆角矩形 + 指向 Tail 的三角尾；Text 允许为空。</summary>
public sealed record SpeechBubbleAnnotation(
    Rect Rect,
    Point Tail,
    string Text,
    uint Color,
    double Width,
    double FontSize,
    ShapeFill Fill = ShapeFill.None,
    double Opacity = 1,
    double Rotation = 0,
    uint FillColor = 0) : IAnnotation;

/// <summary>水印：在 Area 内按 Spacing 平铺 Text，整体绕区域中心旋转 Angle 度。</summary>
public sealed record WatermarkAnnotation(
    Rect Area,
    string Text,
    uint Color,
    double FontSize,
    double Opacity = 0.35,
    double Angle = 30,
    double Spacing = 160) : IAnnotation;

public sealed record PenAnnotation(
    IReadOnlyList<Point> Points,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid,
    LineStyle Style = LineStyle.Neat) : IAnnotation;

/// <summary>马赛克：像素化或模糊一块区域。Cell 是像素格边长（DIP）。</summary>
public sealed record MosaicAnnotation(Rect Rect, int Cell, bool Blur) : IAnnotation;

/// <summary>文字：Origin 是左下角基线起点（和 Skia 的 DrawText 对齐）。</summary>
/// <summary>序号徽章外形（参考 LumaShot 的 NumberShape）。</summary>
public enum NumberShape
{
    /// <summary>实心圆</summary>
    SolidCircle,

    /// <summary>描边圆：无填充 + 纯色描边 + 同色文字</summary>
    OutlineCircle,

    /// <summary>双层圆：外环 + 内实心圆</summary>
    DoubleCircle,

    /// <summary>实心方块</summary>
    SolidSquare,

    /// <summary>圆角方块</summary>
    RoundedSquare,

    /// <summary>胶囊标签：全圆角</summary>
    Capsule,

    /// <summary>旗标：底边中央 V 形缺口</summary>
    Flag,

    /// <summary>定位针：圆头 + 尖底</summary>
    Pin,

    /// <summary>六边形：尖顶朝上</summary>
    Hexagon,

    /// <summary>角标标签：右上直角三角</summary>
    Corner
}

/// <summary>文字水平对齐。</summary>
public enum TextAlign
{
    Left,
    Center,
    Right
}

public sealed record TextAnnotation(
    Point Origin,
    string Text,
    uint Color,
    double FontSize,
    bool Bold = false,
    double Rotation = 0,
    string FontFamily = "",
    bool Italic = false,
    TextAlign Align = TextAlign.Left,
    uint BackgroundColor = 0,
    uint StrokeColor = 0,
    double StrokeWidth = 0) : IAnnotation;

/// <summary>序号：圆形底 + 数字，Number 从 1 自动递增；Label 非空时覆盖自动序号。</summary>
public sealed record NumberAnnotation(
    Point Center,
    int Number,
    uint Color,
    double Size,
    NumberFormat Format = NumberFormat.Arabic,
    string? Label = null,
    NumberShape Shape = NumberShape.SolidCircle) : IAnnotation;

/// <summary>箭头两翼的几何；单独抽出来便于测试。</summary>
public static class ArrowGeometry
{
    /// <summary>箭头头部左右两翼（以终点为顶点，指向起点方向）。</summary>
    public static (Point Left, Point Right) Head(Point from, Point to, double size)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < 0.001 || size <= 0)
            return (to, to);

        var angle = Math.Atan2(dy, dx) + Math.PI;
        const double wing = 0.45; // 约 26°

        return (
            new Point(to.X + size * Math.Cos(angle - wing), to.Y + size * Math.Sin(angle - wing)),
            new Point(to.X + size * Math.Cos(angle + wing), to.Y + size * Math.Sin(angle + wing)));
    }
}

/// <summary>
/// 标注样式的共用规则：填充色回退、填充透明度、菱形顶点。
/// 渲染（Skia）、预览（Avalonia）和导出都走这里，避免各写一份。
/// </summary>
public static class AnnotationStyle
{
    /// <summary>填充色：0 表示跟随描边色。</summary>
    public static uint FillColor(uint strokeColor, uint fillColor) =>
        fillColor != 0 ? fillColor : strokeColor;

    /// <summary>填充透明度：实心全量，淡色 28%，图案类用一半。</summary>
    public static double FillAlpha(ShapeFill fill, double opacity) => fill switch
    {
        ShapeFill.Solid => opacity,
        ShapeFill.Soft => opacity * 0.28,
        _ => opacity * 0.5
    };

    /// <summary>菱形四个顶点（矩形四边中点）。</summary>
    public static Point[] DiamondPoints(Rect rect) =>
    [
        new Point(rect.Center.X, rect.Y),
        new Point(rect.Right, rect.Center.Y),
        new Point(rect.Center.X, rect.Bottom),
        new Point(rect.X, rect.Center.Y)
    ];
}

/// <summary>
/// 把属性栏的样式修改落到一条标注上。只改支持该属性的图元，其它类型原样返回；
/// 属性栏改选中对象和改新建默认值共用这一份，避免每个 setter 各写一条 switch。
/// </summary>
public static class AnnotationStyler
{
    /// <summary>反查标注对应哪个工具，属性栏据此显示它支持的属性组。</summary>
    public static AnnotationTool ToolOf(IAnnotation note) => note switch
    {
        ShapeAnnotation { Diamond: true } => AnnotationTool.Diamond,
        ShapeAnnotation { Ellipse: true } => AnnotationTool.Ellipse,
        ShapeAnnotation => AnnotationTool.Rectangle,
        ArrowAnnotation => AnnotationTool.Arrow,
        LineAnnotation => AnnotationTool.Line,
        DistanceAnnotation => AnnotationTool.Distance,
        AngleAnnotation => AnnotationTool.Angle,
        BracketAnnotation => AnnotationTool.Bracket,
        SpeechBubbleAnnotation => AnnotationTool.Bubble,
        WatermarkAnnotation => AnnotationTool.Watermark,
        PenAnnotation => AnnotationTool.Pen,
        MosaicAnnotation => AnnotationTool.Mosaic,
        TextAnnotation => AnnotationTool.Text,
        NumberAnnotation => AnnotationTool.Number,
        SpotlightAnnotation => AnnotationTool.Spotlight,
        MagnifierAnnotation => AnnotationTool.Magnifier,
        _ => AnnotationTool.None
    };

    public static IAnnotation WithColor(IAnnotation note, uint color) => note switch
    {
        ShapeAnnotation s => s with { Color = color },
        ArrowAnnotation a => a with { Color = color },
        LineAnnotation l => l with { Color = color },
        DistanceAnnotation d => d with { Color = color },
        AngleAnnotation g => g with { Color = color },
        BracketAnnotation b => b with { Color = color },
        SpeechBubbleAnnotation b => b with { Color = color },
        WatermarkAnnotation w => w with { Color = color },
        PenAnnotation p => p with { Color = color },
        TextAnnotation t => t with { Color = color },
        NumberAnnotation n => n with { Color = color },
        MagnifierAnnotation m => m with { BorderColor = color },
        _ => note
    };

    public static IAnnotation WithWidth(IAnnotation note, double width) => note switch
    {
        ShapeAnnotation s => s with { Width = width },
        ArrowAnnotation a => a with { Width = width },
        LineAnnotation l => l with { Width = width },
        DistanceAnnotation d => d with { Width = width },
        AngleAnnotation g => g with { Width = width },
        BracketAnnotation b => b with { Width = width },
        SpeechBubbleAnnotation b => b with { Width = width },
        PenAnnotation p => p with { Width = width },
        _ => note
    };

    public static IAnnotation WithDash(IAnnotation note, LineDash dash) => note switch
    {
        ShapeAnnotation s => s with { Dash = dash },
        ArrowAnnotation a => a with { Dash = dash },
        LineAnnotation l => l with { Dash = dash },
        DistanceAnnotation d => d with { Dash = dash },
        BracketAnnotation b => b with { Dash = dash },
        PenAnnotation p => p with { Dash = dash },
        _ => note
    };

    public static IAnnotation WithLineStyle(IAnnotation note, LineStyle style) => note switch
    {
        ShapeAnnotation s => s with { Style = style },
        ArrowAnnotation a => a with { Style = style },
        LineAnnotation l => l with { Style = style },
        DistanceAnnotation d => d with { Style = style },
        BracketAnnotation b => b with { Style = style },
        PenAnnotation p => p with { Style = style },
        _ => note
    };

    public static IAnnotation WithOpacity(IAnnotation note, double opacity) => note switch
    {
        ShapeAnnotation s => s with { Opacity = opacity },
        ArrowAnnotation a => a with { Opacity = opacity },
        LineAnnotation l => l with { Opacity = opacity },
        DistanceAnnotation d => d with { Opacity = opacity },
        AngleAnnotation g => g with { Opacity = opacity },
        BracketAnnotation b => b with { Opacity = opacity },
        SpeechBubbleAnnotation b => b with { Opacity = opacity },
        WatermarkAnnotation w => w with { Opacity = opacity },
        PenAnnotation p => p with { Opacity = opacity },
        SpotlightAnnotation s => s with { Opacity = opacity },
        _ => note
    };

    public static IAnnotation WithFill(IAnnotation note, ShapeFill fill) => note switch
    {
        ShapeAnnotation s => s with { Fill = fill },
        SpeechBubbleAnnotation b => b with { Fill = fill },
        _ => note
    };

    public static IAnnotation WithFillColor(IAnnotation note, uint color) => note switch
    {
        ShapeAnnotation s => s with { FillColor = color },
        SpeechBubbleAnnotation b => b with { FillColor = color },
        _ => note
    };

    public static IAnnotation WithRadius(IAnnotation note, double radius) => note switch
    {
        ShapeAnnotation s => s with { Radius = radius },
        _ => note
    };

    public static IAnnotation WithFontSize(IAnnotation note, double size) => note switch
    {
        TextAnnotation t => t with { FontSize = size },
        WatermarkAnnotation w => w with { FontSize = size },
        SpeechBubbleAnnotation b => b with { FontSize = size },
        _ => note
    };

    public static IAnnotation WithBold(IAnnotation note, bool bold) => note switch
    {
        TextAnnotation t => t with { Bold = bold },
        _ => note
    };

    public static IAnnotation WithNumberSize(IAnnotation note, double size) => note switch
    {
        NumberAnnotation n => n with { Size = size },
        _ => note
    };

    public static IAnnotation WithNumber(IAnnotation note, int number) => note switch
    {
        NumberAnnotation n => n with { Number = number },
        _ => note
    };

    public static IAnnotation WithNumberFormat(IAnnotation note, NumberFormat format) => note switch
    {
        NumberAnnotation n => n with { Format = format },
        _ => note
    };

    public static IAnnotation WithNumberLabel(IAnnotation note, string? label) => note switch
    {
        NumberAnnotation n => n with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() },
        _ => note
    };

    public static IAnnotation WithNumberShape(IAnnotation note, NumberShape shape) => note switch
    {
        NumberAnnotation n => n with { Shape = shape },
        _ => note
    };

    public static IAnnotation WithWatermarkText(IAnnotation note, string text) => note switch
    {
        WatermarkAnnotation w => w with { Text = text },
        _ => note
    };

    public static IAnnotation WithFontFamily(IAnnotation note, string family) => note switch
    {
        TextAnnotation t => t with { FontFamily = family },
        _ => note
    };

    public static IAnnotation WithItalic(IAnnotation note, bool italic) => note switch
    {
        TextAnnotation t => t with { Italic = italic },
        _ => note
    };

    public static IAnnotation WithTextAlign(IAnnotation note, TextAlign align) => note switch
    {
        TextAnnotation t => t with { Align = align },
        _ => note
    };

    public static IAnnotation WithTextBackground(IAnnotation note, uint color) => note switch
    {
        TextAnnotation t => t with { BackgroundColor = color },
        _ => note
    };

    public static IAnnotation WithTextStroke(IAnnotation note, uint color, double width) => note switch
    {
        TextAnnotation t => t with { StrokeColor = color, StrokeWidth = width },
        _ => note
    };

    public static IAnnotation WithMosaic(IAnnotation note, int cell, bool blur) => note switch
    {
        MosaicAnnotation m => m with { Cell = cell, Blur = blur },
        _ => note
    };
}

/// <summary>序号文本：手动标签优先，否则按数字类型格式化。</summary>
public static class NumberText
{
    public static string Format(int number, NumberFormat format, string? label = null)
    {
        if (!string.IsNullOrWhiteSpace(label))
            return label.Trim();

        var value = Math.Max(0, number);

        return format switch
        {
            NumberFormat.Roman => Roman(value),
            NumberFormat.LowerLetter => Letters(value, 'a'),
            NumberFormat.UpperLetter => Letters(value, 'A'),
            NumberFormat.Chinese => Chinese(value),
            _ => value.ToString()
        };
    }

    /// <summary>罗马数字只到 3999，超出回退阿拉伯数字。</summary>
    private static string Roman(int value)
    {
        if (value is < 1 or > 3999)
            return value.ToString();

        (int Value, string Symbol)[] table =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];

        var rest = value;
        var result = new StringBuilder();

        foreach (var (symbolValue, symbol) in table)
        {
            while (rest >= symbolValue)
            {
                result.Append(symbol);
                rest -= symbolValue;
            }
        }

        return result.ToString();
    }

    /// <summary>字母按双射二十六进制：1=a，26=z，27=aa。</summary>
    private static string Letters(int value, char baseline)
    {
        if (value <= 0)
            return value.ToString();

        var rest = value;
        var result = new StringBuilder();

        while (rest > 0)
        {
            rest--;
            result.Insert(0, (char)(baseline + rest % 26));
            rest /= 26;
        }

        return result.ToString();
    }

    /// <summary>中文数字：支持到「兆」段；段内按 千/百/十 处理，中间跳段补「零」。</summary>
    private static string Chinese(int value)
    {
        if (value == 0)
            return "零";

        if (value < 0)
            return value.ToString();

        string[] bigUnits = ["", "万", "亿", "兆"];
        var parts = new List<(int Big, int Section, string Text)>();
        var rest = value;
        var bigIndex = 0;

        while (rest > 0 && bigIndex < bigUnits.Length)
        {
            var section = rest % 10000;
            rest /= 10000;

            if (section > 0)
            {
                var text = FormatSection(section);
                if (bigUnits[bigIndex].Length > 0)
                    text += bigUnits[bigIndex];

                parts.Add((bigIndex, section, text));
            }

            bigIndex++;
        }

        if (parts.Count == 0)
            return "零";

        parts.Reverse();   // 从高位段往低位段拼

        var result = new StringBuilder();

        for (var index = 0; index < parts.Count; index++)
        {
            if (index > 0 && parts[index - 1].Big - parts[index].Big == 1
                && parts[index].Section < 1000)
            {
                result.Append('零');
            }

            result.Append(parts[index].Text);
        }

        return result.ToString();
    }

    private static string FormatSection(int section)
    {
        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        string[] units = ["千", "百", "十", ""];

        var digit = new[]
        {
            section / 1000 % 10, section / 100 % 10, section / 10 % 10, section % 10
        };

        var result = new StringBuilder();
        var zeroPending = false;

        for (var index = 0; index < 4; index++)
        {
            if (digit[index] == 0)
            {
                // 后面还有非零，且已经写过内容，才需要补「零」
                if (result.Length > 0 && HasNonZero(digit, index + 1))
                    zeroPending = true;

                continue;
            }

            if (zeroPending)
            {
                result.Append('零');
                zeroPending = false;
            }

            // 「一十」开头省掉「一」：10 → 十，15 → 十五
            if (!(digit[index] == 1 && index == 2 && result.Length == 0))
                result.Append(digits[digit[index]]);

            result.Append(units[index]);
        }

        return result.ToString();
    }

    private static bool HasNonZero(int[] digit, int from)
    {
        for (var index = from; index < digit.Length; index++)
        {
            if (digit[index] != 0)
                return true;
        }

        return false;
    }
}

/// <summary>
/// 序号徽章几何：外接框、多边形顶点、文字锚点。
/// 渲染（Skia）和预览（Avalonia）共用同一份坐标，避免两套算法对不齐。
/// </summary>
internal static class NumberGeometry
{
    /// <summary>
    /// 外接框：胶囊宽 1.5×、定位针高 1.3×（和 LumaShot 的创建规则一致）；
    /// 有自定义标签时按文字宽度撑开，保证标签放得下。
    /// </summary>
    public static Rect BadgeBounds(Point center, double size, NumberShape shape, double textWidth = 0)
    {
        var width = size;
        var height = size;

        if (shape == NumberShape.Capsule)
            width = size * 1.5;
        else if (shape == NumberShape.Pin)
            height = size * 1.3;

        if (textWidth > 0)
            width = Math.Max(width, textWidth + size * 0.9);

        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    /// <summary>选中/缩放用的外接框，按实际文字宽度算。</summary>
    public static Rect Bounds(NumberAnnotation note)
    {
        var text = NumberText.Format(note.Number, note.Format, note.Label);
        using var font = AnnotationText.CreateFont(note.Size * 0.55);
        return BadgeBounds(note.Center, note.Size, note.Shape, font.MeasureText(text));
    }

    /// <summary>多边形外形的顶点；圆 / 方 / 胶囊 / 定位针返回 null（它们有各自的画法）。</summary>
    public static IReadOnlyList<Point>? Polygon(NumberShape shape, Rect bounds)
    {
        switch (shape)
        {
            case NumberShape.Flag:
            {
                var radius = Math.Min(bounds.Width, bounds.Height) / 2;
                return new[]
                {
                    new Point(bounds.Left, bounds.Top),
                    new Point(bounds.Right, bounds.Top),
                    new Point(bounds.Right, bounds.Bottom),
                    new Point(bounds.Center.X, bounds.Bottom - radius * 0.5),
                    new Point(bounds.Left, bounds.Bottom)
                };
            }

            case NumberShape.Hexagon:
            {
                var radius = Math.Min(bounds.Width, bounds.Height) / 2;
                var center = bounds.Center;
                var points = new List<Point>(6);

                for (var index = 0; index < 6; index++)
                {
                    var angle = Math.PI * index / 3 - Math.PI / 2;
                    points.Add(new Point(
                        center.X + radius * Math.Cos(angle),
                        center.Y + radius * Math.Sin(angle)));
                }

                return points;
            }

            case NumberShape.Corner:
                return new[]
                {
                    new Point(bounds.Left, bounds.Top),
                    new Point(bounds.Right, bounds.Top),
                    new Point(bounds.Right, bounds.Bottom)
                };

            default:
                return null;
        }
    }

    /// <summary>文字锚点：定位针在圆头里、旗标上移避开缺口、角标靠右上，其余居中。</summary>
    public static Point TextAnchor(Rect bounds, NumberShape shape)
    {
        var radius = Math.Min(bounds.Width, bounds.Height) / 2;

        switch (shape)
        {
            case NumberShape.Pin:
                return new Point(bounds.Center.X, bounds.Top + radius);

            case NumberShape.Flag:
                return new Point(bounds.Center.X, (bounds.Top + bounds.Bottom - radius * 0.45) / 2);

            case NumberShape.Corner:
                return new Point(
                    (bounds.Left + bounds.Width * 0.4 + bounds.Right) / 2,
                    (bounds.Top + bounds.Bottom - bounds.Height * 0.4) / 2);

            default:
                return bounds.Center;
        }
    }

    /// <summary>定位针的圆头参数：圆心、半径、尖底点、两个切点。</summary>
    public static (Point Center, double Radius, Point Tip, Point Left, Point Right) Pin(Rect bounds)
    {
        var radius = Math.Min(bounds.Width / 2, bounds.Height / 2.6);
        var center = new Point(bounds.Center.X, bounds.Top + radius);
        var tip = new Point(bounds.Center.X, bounds.Bottom);

        var distance = tip.Y - center.Y;
        var dy = distance > 0.001 ? radius * radius / distance : 0;
        var dx = Math.Sqrt(Math.Max(0, radius * radius - dy * dy));

        return (
            center,
            radius,
            tip,
            new Point(center.X - dx, center.Y + dy),
            new Point(center.X + dx, center.Y + dy));
    }
}

/// <summary>一次截图会话里的标注集合，带撤销/重做。撤销栈记录的是「增删改」而不是整条列表。</summary>
public sealed class AnnotationDocument
{
    private abstract record Change;

    private sealed record Added(int Index, IAnnotation Item) : Change;

    private sealed record Removed(int Index, IAnnotation Item) : Change;

    private sealed record Replaced(int Index, IAnnotation Before, IAnnotation After) : Change;

    private readonly List<IAnnotation> _items = [];
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();

    public IReadOnlyList<IAnnotation> Items => _items;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Add(IAnnotation annotation)
    {
        var index = _items.Count;
        _items.Add(annotation);
        _undo.Push(new Added(index, annotation));
        _redo.Clear();
    }

    /// <summary>就地替换，不记录历史；拖动中的逐帧预览用这个。</summary>
    public void Replace(int index, IAnnotation annotation)
    {
        if (index < 0 || index >= _items.Count)
            return;

        _items[index] = annotation;
    }

    /// <summary>替换并记一条历史；文字编辑等一次性修改用这个。</summary>
    public void ReplaceWithHistory(int index, IAnnotation annotation)
    {
        if (index < 0 || index >= _items.Count)
            return;

        var before = _items[index];
        if (Equals(before, annotation))
            return;

        _items[index] = annotation;
        _undo.Push(new Replaced(index, before, annotation));
        _redo.Clear();
    }

    /// <summary>拖动结束后把「原图元 → 当前图元」记成一条历史（当前已由 Replace 应用）。</summary>
    public void RecordReplace(int index, IAnnotation before, IAnnotation after)
    {
        if (index < 0 || index >= _items.Count || Equals(before, after))
            return;

        _undo.Push(new Replaced(index, before, after));
        _redo.Clear();
    }

    /// <summary>删除并记录历史；撤销会把它插回原位置。</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        var item = _items[index];
        _items.RemoveAt(index);
        _undo.Push(new Removed(index, item));
        _redo.Clear();
    }

    /// <summary>整批替换（选区平移/撤销回放用），会清空历史，避免索引失效。</summary>
    public void SetItems(IEnumerable<IAnnotation> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _undo.Clear();
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        var change = _undo.Pop();
        Apply(change, undo: true);
        _redo.Push(change);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;

        var change = _redo.Pop();
        Apply(change, undo: false);
        _undo.Push(change);
    }

    private void Apply(Change change, bool undo)
    {
        switch (change)
        {
            case Added added:
                if (undo)
                {
                    if (added.Index >= 0 && added.Index < _items.Count)
                        _items.RemoveAt(added.Index);
                    else
                        _items.Remove(added.Item);
                }
                else
                {
                    _items.Insert(Math.Min(added.Index, _items.Count), added.Item);
                }

                break;

            case Removed removed:
                if (undo)
                {
                    _items.Insert(Math.Min(removed.Index, _items.Count), removed.Item);
                }
                else if (removed.Index >= 0 && removed.Index < _items.Count)
                {
                    _items.RemoveAt(removed.Index);
                }

                break;

            case Replaced replaced:
                if (replaced.Index >= 0 && replaced.Index < _items.Count)
                    _items[replaced.Index] = undo ? replaced.Before : replaced.After;
                break;
        }
    }

    public void Clear()
    {
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
    }
}
