// UiElementProbe 分部类：子节点索引与扁平树类型。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

public sealed partial class UiElementProbe
{
    /// <summary>
    /// 子节点空间索引：均匀网格（R-tree 的简化版）。
    /// 只需要「点在哪个格子里」，不需要维护树的平衡，常数更小。
    /// </summary>
    private sealed class ChildIndex
    {
        private const int CellSize = 64;

        /// <summary>跨太多格的子节点单独放全局列表，避免索引膨胀。</summary>
        private const int MaxCellsPerChild = 64;

        private readonly PixelRect _bounds;
        private readonly Dictionary<(int X, int Y), List<(int Order, Node Node)>> _cells = [];
        private readonly List<(int Order, Node Node)> _global = [];

        public ChildIndex(IReadOnlyList<Node> children, PixelRect bounds)
        {
            _bounds = bounds;

            for (var order = 0; order < children.Count; order++)
            {
                var child = children[order];
                var minX = (child.Bounds.X - bounds.X) / CellSize;
                var minY = (child.Bounds.Y - bounds.Y) / CellSize;
                var maxX = (child.Bounds.X + child.Bounds.Width - 1 - bounds.X) / CellSize;
                var maxY = (child.Bounds.Y + child.Bounds.Height - 1 - bounds.Y) / CellSize;
                var cells = (long)(maxX - minX + 1) * (maxY - minY + 1);

                if (cells <= 0 || cells > MaxCellsPerChild)
                {
                    _global.Add((order, child));
                    continue;
                }

                for (var y = minY; y <= maxY; y++)
                {
                    for (var x = minX; x <= maxX; x++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list))
                            _cells[(x, y)] = list = [];

                        list.Add((order, child));
                    }
                }
            }
        }

        public Node? Hit(PixelPoint point)
        {
            var cellX = (point.X - _bounds.X) / CellSize;
            var cellY = (point.Y - _bounds.Y) / CellSize;
            var bestOrder = -1;
            Node? best = null;

            foreach (var (order, child) in _global)
                Consider(order, child);

            if (_cells.TryGetValue((cellX, cellY), out var cell))
            {
                foreach (var (order, child) in cell)
                    Consider(order, child);
            }

            return best;

            void Consider(int order, Node child)
            {
                // 后面的兄弟更靠前：取 order 最大的包含光标的节点
                if (order > bestOrder && Contains(child.Bounds, point))
                {
                    bestOrder = order;
                    best = child;
                }
            }
        }
    }

    /// <summary>预建时缓存的扁平 UIA 元素；遮罩打开后只在这里做包含命中。</summary>
    private sealed class FlatElement(
        PixelRect bounds,
        int controlType,
        IUIAutomationElement element)
    {
        public PixelRect Bounds { get; } = bounds;
        public int ControlType { get; } = controlType;
        public IUIAutomationElement Element { get; } = element;
        public bool Structural => IsStructural(ControlType);
    }

    /// <summary>
    /// 一个窗口的预建结果：扁平元素 + 窗口矩形。
    /// 窗口矩形和元素存在一起，多浏览器预建时不会因为树缓存被回收而失效。
    /// </summary>
    private sealed class FlatTree(List<FlatElement> elements, PixelRect bounds)
    {
        public List<FlatElement> Elements { get; } = elements;
        public PixelRect Bounds { get; } = bounds;
    }

    /// <summary>懒展开的一层控件节点。</summary>
    private sealed class Node(
        PixelRect bounds,
        int controlType,
        IUIAutomationElement element,
        Node? parent,
        PixelRect clip)
    {
        public PixelRect Bounds { get; } = bounds;
        public int ControlType { get; } = controlType;
        public IUIAutomationElement Element { get; } = element;
        public Node? Parent { get; } = parent;

        /// <summary>整次探测的裁剪边界（root 的窗口矩形）；子节点继承。</summary>
        public PixelRect Clip { get; } = clip;

        public List<Node>? Children { get; set; }
        public ChildIndex? Index { get; set; }
        public bool ExpandFailed { get; set; }
        public int Failures { get; set; }
        public int EmptyExpansions { get; set; }

        /// <summary>解码中途停下时保留的原始子节点数组和断点位置。</summary>
        public IUIAutomationElementArray? PendingChildren { get; set; }

        public int PendingIndex { get; set; }

        public List<Node> PendingEntries { get; } = [];

        public bool Structural => IsStructural(ControlType);
    }
}
