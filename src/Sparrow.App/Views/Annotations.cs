using Avalonia;

namespace Sparrow.App.Views;

/// <summary>截图标注工具。</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
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
    LineDash Dash = LineDash.Solid,
    double Rotation = 0) : IAnnotation;

public sealed record ArrowAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid) : IAnnotation;

/// <summary>直线：只有线身，没有箭头。</summary>
public sealed record LineAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid) : IAnnotation;

/// <summary>测距：线 + 两端刻度 + 长度标签；Label 在创建时按当时的单位算好，切单位不回溯。</summary>
public sealed record DistanceAnnotation(
    Point From,
    Point To,
    uint Color,
    double Width,
    string Label,
    double Opacity = 1,
    LineDash Dash = LineDash.Solid) : IAnnotation;

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
    double Rotation = 0) : IAnnotation
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
    double Rotation = 0) : IAnnotation;

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
    LineDash Dash = LineDash.Solid) : IAnnotation;

/// <summary>马赛克：像素化或模糊一块区域。Cell 是像素格边长（DIP）。</summary>
public sealed record MosaicAnnotation(Rect Rect, int Cell, bool Blur) : IAnnotation;

/// <summary>文字：Origin 是左下角基线起点（和 Skia 的 DrawText 对齐）。</summary>
public sealed record TextAnnotation(
    Point Origin,
    string Text,
    uint Color,
    double FontSize,
    bool Bold = false,
    double Rotation = 0) : IAnnotation;

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
