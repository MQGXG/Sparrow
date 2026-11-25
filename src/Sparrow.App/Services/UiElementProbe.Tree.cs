// UiElementProbe 分部类：子窗口枚举、根节点构建与超时。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

public sealed partial class UiElementProbe
{
    /// <summary>释放除当前窗口以外的控件树（每个窗口的树可能上千个元素）。</summary>
    private void EvictOtherTrees(IntPtr keep)
    {
        if (_trees.Count == 0)
            return;

        var keepTree = _trees.TryGetValue(keep, out var kept) ? kept : null;

        foreach (var (handle, root) in _trees)
        {
            if (handle != keep)
                ReleaseTree(root);
        }

        _trees.Clear();

        if (keepTree is not null)
            _trees[keep] = keepTree;

        _nodeCount = keepTree is null ? 0 : CountNodes(keepTree);
    }

    private static void ReleaseTree(Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            ReleaseCom(node.Element);
            ReleaseCom(node.PendingChildren);

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                    pending.Push(child);
            }

            // 解码中断时已建好但还没挂到 Children 上的节点也要释放
            foreach (var entry in node.PendingEntries)
                pending.Push(entry);
        }
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // 已释放 / 代理异常：忽略
        }
    }

    private static int CountNodes(Node root)
    {
        var count = 0;
        var pending = new Stack<Node>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            count++;

            if (node.Children is null)
                continue;

            foreach (var child in node.Children)
                pending.Push(child);
        }

        return count;
    }

    /// <summary>包含光标的子窗口，面积小的（更深的）排前面。</summary>
    private void PingRenderChildren(IntPtr hwnd)
    {
        EnumChildWindows(hwnd, (child, _) =>
        {
            var className = GetClassName(child);

            if (IsRenderHostClass(className) && _accessibilityPinged.Add(child))
                PingAccessibility(child);

            return true;
        }, IntPtr.Zero);
    }

    private List<IntPtr> ChildWindowsAt(IntPtr hwnd, PixelPoint point)
    {
        // 每次重新枚举：Chrome 的渲染子窗口会在无障碍树初始化时重建句柄，
        // 缓存一次空结果会让整个截图都拿不到网页元素。
        var children = EnumerateChildWindows(hwnd);
        var result = new List<IntPtr>(children.Count);

        // Chrome 的网页无障碍需要一次 IAccessible 查询才会真正建树。
        // 先 ping 渲染子窗口，再走 UIA，否则树里只有分组/文档。
        foreach (var child in children)
        {
            if (IsRenderHostClass(GetClassName(child)) && _accessibilityPinged.Add(child))
                PingAccessibility(child);
        }

        foreach (var child in children)
        {
            if (ContainsWindowPoint(child, point))
                result.Add(child);
        }

        result.Sort((left, right) => WindowArea(left).CompareTo(WindowArea(right)));
        SelectorTrace.Write($"children hwnd=0x{hwnd.ToInt64():X} total={children.Count} at-point={result.Count}");
        return result.Count > MaxChildWindows ? result.GetRange(0, MaxChildWindows) : result;
    }

    private static List<IntPtr> EnumerateChildWindows(IntPtr hwnd)
    {
        var children = new List<IntPtr>();

        EnumChildWindows(hwnd, (child, _) =>
        {
            if (WindowArea(child) <= 0)
                return true;

            // 正常情况下只取可见子窗口；Chrome 的渲染宿主在树初始化瞬间
            // 可能短暂不可见，按类名兜底，不能把网页内容漏掉。
            var visible = IsWindowVisible(child);
            var className = GetClassName(child);

            if (!visible && !IsRenderHostClass(className))
                return true;

            children.Add(child);
            SelectorTrace.Write(
                $"child-enum 0x{child.ToInt64():X} visible={visible} class={className} " +
                $"{WindowRectText(child)}");
            return true;
        }, IntPtr.Zero);

        return children;
    }

    private static bool IsRenderHostClass(string? className) =>
        className is not null
        && (className.Contains("RenderWidget", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Chrome_RenderWidgetHost", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)
            || className.Contains("WebView", StringComparison.OrdinalIgnoreCase));

    private static string WindowRectText(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var rect)
            ? $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}"
            : "no-rect";

    private static long WindowArea(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var rect)
            ? Math.Max(0L, (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top))
            : 0L;

    private static bool ContainsWindowPoint(IntPtr hwnd, PixelPoint point) =>
        GetWindowRect(hwnd, out var rect) &&
        point.X >= rect.Left && point.X < rect.Right &&
        point.Y >= rect.Top && point.Y < rect.Bottom;

    private Node? BuildRoot(
        IntPtr hwnd,
        TimeSpan remaining,
        TimeSpan callLimit,
        IUIAutomationCacheRequest? request)
    {
        if (_automation is null || request is null)
            return null;

        ApplyTimeouts(remaining, callLimit);

        try
        {
            var element = _automation.ElementFromHandleBuildCache(hwnd, request);
            if (element is null)
                return null;

            // 根节点的矩形就是整次探测的裁剪边界：子节点超出窗口的部分直接裁掉
            var rect = element.CachedBoundingRectangle;
            var clip = new PixelRect(
                rect.left,
                rect.top,
                rect.right - rect.left,
                rect.bottom - rect.top);

            return CreateNode(element, parent: null, clip);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 每次跨进程调用前把 UIA 超时收在剩余预算内：
    /// 前景最多 168ms、细化最多 500ms，绝不把整次探测拖到卡住。
    /// </summary>
    private void ApplyTimeouts(TimeSpan remaining, TimeSpan callLimit)
    {
        if (_automation is null)
            return;

        var limit = Math.Max(1, (int)Math.Ceiling(callLimit.TotalMilliseconds));
        var milliseconds = (uint)Math.Clamp(
            remaining.TotalMilliseconds, 1, limit);

        try
        {
            _automation.ConnectionTimeout = milliseconds;
            _automation.TransactionTimeout = milliseconds;
        }
        catch
        {
            // 设置超时失败不影响本次探测
        }
    }
}
