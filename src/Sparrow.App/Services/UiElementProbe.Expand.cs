// UiElementProbe 分部类：节点展开、命中与路径构建。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

public sealed partial class UiElementProbe
{
    private enum ExpandOutcome
    {
        Expanded,
        BudgetExhausted,
        DecodingPending,
        ProviderTimeout,
        ProviderFailure,
        TraversalLimit
    }

    /// <summary>取回这一层子节点（带缓存属性），每个节点一辈子只展开一次。</summary>
    private ExpandOutcome TryExpand(Node node, ProbeState state)
    {
        if (node.Children is not null)
            return ExpandOutcome.Expanded;

        if (node.ExpandFailed || node.Element is null || state.Request is null)
            return ExpandOutcome.ProviderFailure;

        // 预算已经用完：这次先不调跨进程，留给细化
        if (state.Expired)
            return ExpandOutcome.BudgetExhausted;

        // 异常大树到上限就不再往下展开，宁可退回窗口级也不撑爆内存
        if (_nodeCount >= MaxNodesPerWindow)
            return ExpandOutcome.TraversalLimit;

        try
        {
            // 第一次展开：把整批子节点数组缓存下来，等会儿逐个解码
            if (node.PendingChildren is null)
            {
                ApplyTimeouts(state.Remaining, state.CallLimit);

                // 一次调用同时刷新本节点缓存并带回子节点集合
                var updated = node.Element.BuildUpdatedCache(state.Request);
                node.PendingChildren = updated.GetCachedChildren();
                node.PendingIndex = 0;
                node.PendingEntries.Clear();
            }

            var array = node.PendingChildren;
            var count = array is null ? 0 : Math.Min(array.Length, MaxChildrenPerNode);

            // 解码途中预算到点：保留数组和下标，下一次探测从断点继续，
            // 不用重新支付一次跨进程调用。
            while (node.PendingIndex < count)
            {
                if (state.Expired)
                    return ExpandOutcome.BudgetExhausted;

                if (_nodeCount >= MaxNodesPerWindow)
                    break;

                var element = array!.GetElement(node.PendingIndex);
                node.PendingIndex++;

                if (element is null)
                    continue;

                var child = CreateNode(element, node, node.Clip);
                if (child is not null)
                    node.PendingEntries.Add(child);
            }

            var children = new List<Node>(node.PendingEntries);
            node.PendingChildren = null;
            node.PendingEntries.Clear();
            node.Children = children;

            // 子节点多于 16 个时建空间索引；和 snow-shot 一样，
            // 小列表用线性倒序扫描更快，大列表（浏览器文档）用索引避免逐个比较。
            node.Index = children.Count > LinearChildLimit
                ? new ChildIndex(children, node.Bounds)
                : null;

            // 容器返回空子树：Chromium 首次查询时树还在建，空结果不能永久缓存，
            // 否则这一个窗口之后永远只剩「整窗候选」。
            if (children.Count == 0 && IsContainer(node.ControlType))
            {
                node.Children = null;

                if (++node.EmptyExpansions >= MaxEmptyExpansions)
                {
                    node.ExpandFailed = true;
                    return ExpandOutcome.ProviderFailure;
                }

                return ExpandOutcome.DecodingPending;
            }

            return ExpandOutcome.Expanded;
        }
        catch (Exception error)
        {
            // 偶发超时/应用忙可以在下一次探测重试；连续失败才放弃这个分支
            node.Failures++;

            if (node.Failures >= 2)
                node.ExpandFailed = true;

            return IsTimeout(error)
                ? ExpandOutcome.ProviderTimeout
                : ExpandOutcome.ProviderFailure;
        }
    }

    /// <summary>UIA 的超时通常以 COMException 返回；Win32 超时也一并认。</summary>
    private static bool IsTimeout(Exception error) =>
        error is COMException com &&
        com.HResult is unchecked((int)0x80131505)
            or unchecked((int)0x8001010A)
            or unchecked((int)0x800705B4);

    private Node? CreateNode(IUIAutomationElement element, Node? parent, PixelRect clip)
    {
        if (element.CachedIsOffscreen != 0)
            return null;

        var rect = element.CachedBoundingRectangle;
        var bounds = Intersect(rect.left, rect.top, rect.right, rect.bottom, clip);

        // 整窗、窗框外、小于 2px 的都不算独立控件
        if (bounds.Width < 2 || bounds.Height < 2)
            return null;

        var controlType = element.CachedControlType;

        _nodeCount++;

        return new Node(bounds, controlType, element, parent, clip);
    }

    private static PixelRect Intersect(int left, int top, int right, int bottom, PixelRect clip)
    {
        left = Math.Max(left, clip.X);
        top = Math.Max(top, clip.Y);
        right = Math.Min(right, clip.X + clip.Width);
        bottom = Math.Min(bottom, clip.Y + clip.Height);

        return right <= left || bottom <= top
            ? default
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// 只在最终选中的节点上取一次名字。UIA 的 Name 在浏览器里可能触发
    /// 跨进程文本提取，不能放进每个节点的缓存请求。
    /// </summary>
    private static string? SafeName(IUIAutomationElement element)
    {
        try
        {
            var name = element.CurrentName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static Node? HitTest(Node node, PixelPoint point)
    {
        if (node.Children is null)
            return null;

        if (node.Index is { } spatial)
            return spatial.Hit(point);

        // 后面的兄弟更靠前：从后往前找第一个包含光标的
        for (var index = node.Children.Count - 1; index >= 0; index--)
        {
            var child = node.Children[index];
            if (Contains(child.Bounds, point))
                return child;
        }

        return null;
    }

    private static Node? StructuralAlternative(Node current, PixelPoint point)
    {
        var node = current;

        while (node.Parent is not null)
        {
            if (!node.Structural || !node.Bounds.Equals(node.Parent.Bounds))
                return null;

            var siblings = node.Parent.Children;
            if (siblings is null)
                return null;

            var index = siblings.IndexOf(node);

            for (var other = index - 1; other >= 0; other--)
            {
                if (Contains(siblings[other].Bounds, point))
                    return siblings[other];
            }

            node = node.Parent;
        }

        return null;
    }

    /// <summary>
    /// 从最深处往上收集完整命中路径（深 → 浅），末项补窗口矩形。
    ///
    /// 和 Snow Shot 的 <c>WindowTree::path</c> 一致：
    /// - 与窗口同尺寸、与更深一层同矩形的节点跳过；
    /// - 结构容器也保留（VS/Chromium 前几层全是 Pane），但只给最深两个查名字；
    /// - 最终结果不会因为“最深节点接近整窗”就整条路径丢弃，用户仍可逐层选。
    /// </summary>
    private static List<UiElementCandidate> BuildPath(
        Node current,
        Node root,
        string? windowTitle,
        bool resolveName)
    {
        var nodes = new List<Node>(8);
        var structuralChecked = 0;

        for (var node = current; node is not null && !ReferenceEquals(node, root); node = node.Parent)
        {
            if (nodes.Count >= MaxRectangles - 1)
                break;

            // 与窗口完全同尺寸的节点就是窗口本身；略小（文档/窗格）仍保留，
            // 用户可以在路径里选到这一层。
            if (node.Bounds.Equals(root.Bounds))
                continue;

            if (nodes.Count > 0 && nodes[^1].Bounds.Equals(node.Bounds))
                continue;

            nodes.Add(node);
        }

        var path = new List<UiElementCandidate>(nodes.Count + 1);
        var namedControls = 0;

        foreach (var node in nodes)
        {
            var kind = ControlTypeName(node.ControlType);
            var title = kind;

            if (resolveName)
            {
                if (!node.Structural)
                {
                    // 只为最深的一个具体控件查名字；每条路径最多 3 次跨进程取名字
                    if (namedControls++ == 0)
                        title = SafeName(node.Element) ?? kind;
                }
                else if (structuralChecked++ < 2)
                {
                    // 结构容器只给最深的两个查名字，再往上通常是整块窗格
                    title = SafeName(node.Element) ?? kind;
                }
            }

            path.Add(new UiElementCandidate(node.Bounds, title, kind));
        }

        path.Add(new UiElementCandidate(
            root.Bounds,
            string.IsNullOrWhiteSpace(windowTitle) ? "窗口" : windowTitle,
            "窗口"));

        return path;
    }

    private static bool Contains(PixelRect bounds, PixelPoint point) =>
        point.X >= bounds.X && point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;

    private static bool IsStructural(int controlType) =>
        controlType is UIA_ControlTypeIds.UIA_PaneControlTypeId
            or UIA_ControlTypeIds.UIA_GroupControlTypeId;

    /// <summary>可能装子控件的容器类型：空子树先当「树还没建好」处理。</summary>
    private static bool IsContainer(int controlType) =>
        IsStructural(controlType)
        || controlType is UIA_ControlTypeIds.UIA_WindowControlTypeId
            or UIA_ControlTypeIds.UIA_DocumentControlTypeId
            or UIA_ControlTypeIds.UIA_CustomControlTypeId
            or UIA_ControlTypeIds.UIA_ToolBarControlTypeId
            or UIA_ControlTypeIds.UIA_MenuControlTypeId
            or UIA_ControlTypeIds.UIA_ListControlTypeId
            or UIA_ControlTypeIds.UIA_TreeControlTypeId
            or UIA_ControlTypeIds.UIA_TabControlTypeId
            or UIA_ControlTypeIds.UIA_TableControlTypeId
            or UIA_ControlTypeIds.UIA_DataGridControlTypeId;

    private static string ControlTypeName(int controlTypeId) =>
        ControlTypeNames.TryGetValue(controlTypeId, out var name) ? name : "控件";

    /// <summary>控件类型的中文名；LocalizedControlType 在部分应用里是空串。</summary>
    private static readonly Dictionary<int, string> ControlTypeNames = new()
    {
        [UIA_ControlTypeIds.UIA_ButtonControlTypeId] = "按钮",
        [UIA_ControlTypeIds.UIA_CheckBoxControlTypeId] = "复选框",
        [UIA_ControlTypeIds.UIA_ComboBoxControlTypeId] = "下拉框",
        [UIA_ControlTypeIds.UIA_EditControlTypeId] = "输入框",
        [UIA_ControlTypeIds.UIA_HyperlinkControlTypeId] = "链接",
        [UIA_ControlTypeIds.UIA_ImageControlTypeId] = "图片",
        [UIA_ControlTypeIds.UIA_ListItemControlTypeId] = "列表项",
        [UIA_ControlTypeIds.UIA_MenuItemControlTypeId] = "菜单项",
        [UIA_ControlTypeIds.UIA_RadioButtonControlTypeId] = "单选项",
        [UIA_ControlTypeIds.UIA_SliderControlTypeId] = "滑块",
        [UIA_ControlTypeIds.UIA_ProgressBarControlTypeId] = "进度条",
        [UIA_ControlTypeIds.UIA_TabItemControlTypeId] = "选项卡",
        [UIA_ControlTypeIds.UIA_TreeItemControlTypeId] = "树节点",
        [UIA_ControlTypeIds.UIA_TextControlTypeId] = "文本",
        [UIA_ControlTypeIds.UIA_ToolBarControlTypeId] = "工具栏",
        [UIA_ControlTypeIds.UIA_DataItemControlTypeId] = "数据项",
        [UIA_ControlTypeIds.UIA_DataGridControlTypeId] = "表格",
        [UIA_ControlTypeIds.UIA_TableControlTypeId] = "表格",
        [UIA_ControlTypeIds.UIA_HeaderItemControlTypeId] = "表头",
        [UIA_ControlTypeIds.UIA_SplitButtonControlTypeId] = "下拉按钮",
        [UIA_ControlTypeIds.UIA_DocumentControlTypeId] = "文档",
        [UIA_ControlTypeIds.UIA_PaneControlTypeId] = "窗格",
        [UIA_ControlTypeIds.UIA_GroupControlTypeId] = "分组",
        [UIA_ControlTypeIds.UIA_CustomControlTypeId] = "控件"
    };
}
