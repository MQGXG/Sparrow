using Avalonia;

namespace Sparrow.App.Views;

/// <summary>截图标注工具。</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Highlighter,
    Mosaic,
    Text,
    Number
}

/// <summary>标注线条样式。</summary>
public enum LineDash
{
    Solid,
    Dash,
    Dot
}

/// <summary>形状填充：无 / 淡色（颜色 × 28%） / 实心。</summary>
public enum ShapeFill
{
    None,
    Soft,
    Solid
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
    LineDash Dash = LineDash.Solid) : IAnnotation;

public sealed record ArrowAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid) : IAnnotation;

public sealed record PenAnnotation(
    IReadOnlyList<Point> Points,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid) : IAnnotation;

/// <summary>马赛克：像素化或模糊一块区域。Cell 是像素格边长（DIP）。</summary>
public sealed record MosaicAnnotation(Rect Rect, int Cell, bool Blur) : IAnnotation;

/// <summary>文字：Origin 是左下角基线起点（和 Skia 的 DrawText 对齐）。</summary>
public sealed record TextAnnotation(
    Point Origin,
    string Text,
    uint Color,
    double FontSize,
    bool Bold = false) : IAnnotation;

/// <summary>序号：圆形底 + 数字，Number 从 1 自动递增。</summary>
public sealed record NumberAnnotation(Point Center, int Number, uint Color, double Size) : IAnnotation;

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

/// <summary>一次截图会话里的标注集合，带撤销/重做。</summary>
public sealed class AnnotationDocument
{
    private readonly List<IAnnotation> _items = [];
    private readonly Stack<IAnnotation> _redo = [];

    public IReadOnlyList<IAnnotation> Items => _items;

    public bool CanUndo => _items.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Add(IAnnotation annotation)
    {
        _items.Add(annotation);
        _redo.Clear();
    }

    /// <summary>就地替换一条（拖动编辑时不改变叠放顺序）。</summary>
    public void Replace(int index, IAnnotation annotation)
    {
        if (index < 0 || index >= _items.Count)
            return;

        _items[index] = annotation;
    }

    /// <summary>删除一条。删除会清掉重做栈：撤销仍然是「弹出最后一条」的语义。</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        _items.RemoveAt(index);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_items.Count == 0)
            return;

        _redo.Push(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;

        _items.Add(_redo.Pop());
    }

    public void Clear()
    {
        _items.Clear();
        _redo.Clear();
    }
}
