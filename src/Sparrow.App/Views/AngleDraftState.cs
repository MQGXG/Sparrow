using Avalonia;

namespace Sparrow.App.Views;

/// <summary>
/// 角度工具的两步输入：第一次拖拽定顶点和第一条边，第二次拖拽定第二条边。
///
/// 抽成纯状态机，避免草稿态和「边框优先」「右键回退」缠在一起。
/// </summary>
internal sealed class AngleDraftState
{
    public Point? Vertex { get; private set; }
    public Point? First { get; private set; }
    public bool IsActive => Vertex is not null && First is not null;

    public void Begin(Point vertex, Point first)
    {
        Vertex = vertex;
        First = first;
    }

    /// <summary>用第二条边试算；任一边短于 6 DIP 视为误触，保留草稿。</summary>
    public bool TryBuild(Point second, out (Point Vertex, Point A, Point B) angle)
    {
        angle = default;

        if (Vertex is not { } vertex || First is not { } first)
            return false;

        if (Distance(vertex, first) < 6 || Distance(vertex, second) < 6)
            return false;

        angle = (vertex, first, second);
        return true;
    }

    public void Clear()
    {
        Vertex = null;
        First = null;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
