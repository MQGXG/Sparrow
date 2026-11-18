using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

/// <summary>UI Automation 探测到的一个控件候选。</summary>
public sealed record UiElementCandidate(
    PixelRect Bounds,
    string Title,
    string ControlType);

/// <summary>
/// 按窗口句柄探测光标下的 UI Automation 控件。
///
/// 为什么不直接 <c>AutomationElement.FromPoint</c>：截图遮罩在最上层，
/// 屏幕命中只会拿到遮罩自己。按句柄进目标窗口的树，用矩形命中。
///
/// 速度的关键和 Snow Shot 一样：用 <see cref="IUIAutomationCacheRequest"/>
/// 把「这一层子节点 + 矩形 + 类型 + 名字」一次性批量取回（每层一次跨进程调用），
/// 之后命中完全在内存里查表。逐层懒展开，没走到的分支不付代价。
///
/// COM 接口是 apartment 绑定的，所以整棵树都活在一个专用 MTA 线程上，
/// 调用方只通过 <see cref="ProbeAsync"/> 投递任务，结果回传后再回 UI 线程。
/// 连接/事务超时把无响应应用的代价限制在几百毫秒内。
/// </summary>
public sealed class UiElementProbe : IDisposable
{
    private const int MaxSteps = 80;
    private const int MaxChildrenPerNode = 600;
    private const int MaxRectangles = 100;
    private const int MaxChildWindows = 10;

    /// <summary>单次跨进程调用最多等多久（毫秒）。</summary>
    private const int ConnectionTimeoutMilliseconds = 300;
    private const int TransactionTimeoutMilliseconds = 800;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Dictionary<IntPtr, Node> _trees = [];
    private readonly Dictionary<IntPtr, List<IntPtr>> _childWindows = [];

    private IUIAutomation2? _automation;
    private IUIAutomationCacheRequest? _request;
    private volatile bool _disposed;

    public UiElementProbe()
    {
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Sparrow.UIA"
        };

        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>在专用线程上探测，永不抛异常；失败或无结果时返回 null。</summary>
    /// <param name="stillWanted">每次遍历前询问；返回 false 说明结果已过时，立即放弃。</param>
    public Task<UiElementCandidate?> ProbeAsync(
        IntPtr hwnd,
        PixelPoint point,
        Func<bool>? stillWanted = null)
    {
        if (_disposed)
            return Task.FromResult<UiElementCandidate?>(null);

        var gate = stillWanted ?? AlwaysWanted;
        var completion = new TaskCompletionSource<UiElementCandidate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                UiElementCandidate? candidate = null;

                try
                {
                    // 排队期间鼠标已经移开：连根节点都不用查
                    if (gate())
                        candidate = Probe(hwnd, point, gate);
                }
                catch
                {
                    // 目标应用没有 UIA 树、COM 失败、窗口已关闭等：一律退回窗口级
                }

                completion.TrySetResult(candidate);
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    private static readonly Func<bool> AlwaysWanted = static () => true;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // 正在跑的探测会先跑完；后台线程不阻塞关闭
            _queue.CompleteAdding();
        }
        catch
        {
        }
    }

    private void Run()
    {
        try
        {
            Initialize();
        }
        catch
        {
            _automation = null;
            _request = null;
        }

        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }
        finally
        {
            Release();
        }
    }

    private void Initialize()
    {
        var automation = (IUIAutomation2)new CUIAutomation8Class();
        automation.ConnectionTimeout = ConnectionTimeoutMilliseconds;
        automation.TransactionTimeout = TransactionTimeoutMilliseconds;

        var request = automation.CreateCacheRequest();
        request.TreeScope = TreeScope.TreeScope_Element | TreeScope.TreeScope_Children;
        // ControlView 会去掉纯布局的中间层；Chromium 那种冗余容器交给结构回溯处理
        request.TreeFilter = automation.ControlViewCondition;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        request.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_NamePropertyId);

        _automation = automation;
        _request = request;
    }

    private void Release()
    {
        foreach (var node in EnumerateNodes())
        {
            if (node.Element is null)
                continue;

            try
            {
                Marshal.FinalReleaseComObject(node.Element);
            }
            catch
            {
            }
        }

        _trees.Clear();
        _childWindows.Clear();

        if (_request is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_request);
            }
            catch
            {
            }

            _request = null;
        }

        if (_automation is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_automation);
            }
            catch
            {
            }

            _automation = null;
        }
    }

    private IEnumerable<Node> EnumerateNodes()
    {
        var pending = new Stack<Node>(_trees.Values);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            if (node.Children is null)
                continue;

            foreach (var child in node.Children)
                pending.Push(child);
        }
    }

    private UiElementCandidate? Probe(IntPtr hwnd, PixelPoint point, Func<bool> stillWanted)
    {
        if (_request is null)
            return null;

        var candidate = ProbeWindow(hwnd, point, stillWanted);
        if (candidate is not null)
            return candidate;

        // UWP/WinUI 的顶层是 ApplicationFrameWindow，UIA 树里只有窗框；
        // 真正的内容在 Windows.UI.Core.CoreWindow 这类子窗口里
        foreach (var child in ChildWindowsAt(hwnd, point))
        {
            if (!stillWanted())
                return null;

            candidate = ProbeWindow(child, point, stillWanted);
            if (candidate is not null)
                return candidate;
        }

        return null;
    }

    private UiElementCandidate? ProbeWindow(IntPtr hwnd, PixelPoint point, Func<bool> stillWanted)
    {
        if (!_trees.TryGetValue(hwnd, out var root))
        {
            root = BuildRoot(hwnd);
            if (root is null)
                return null;

            _trees[hwnd] = root;
        }

        if (!Contains(root.Bounds, point))
            return null;

        var current = root;

        for (var step = 0; step < MaxSteps; step++)
        {
            // 长时间遍历期间鼠标可能已经移开，及时收手
            if (!stillWanted())
                return null;

            if (!TryExpand(current))
                break;

            var child = HitTest(current, point);

            if (child is null)
            {
                // UIA 的兄弟顺序不保证堆叠顺序：Chromium 这类应用会用等大的
                // Pane/Group 盖住真正的内容分支，需要沿等大结构节点回溯
                var alternative = StructuralAlternative(current, point);
                if (alternative is null)
                    break;

                current = alternative;
                continue;
            }

            current = child;
        }

        return Pick(current, root);
    }

    /// <summary>包含光标的子窗口，面积小的（更深的）排前面。</summary>
    private List<IntPtr> ChildWindowsAt(IntPtr hwnd, PixelPoint point)
    {
        if (!_childWindows.TryGetValue(hwnd, out var children))
        {
            children = EnumerateChildWindows(hwnd);
            _childWindows[hwnd] = children;
        }

        var result = new List<IntPtr>(children.Count);

        foreach (var child in children)
        {
            if (ContainsWindowPoint(child, point))
                result.Add(child);
        }

        result.Sort((left, right) => WindowArea(left).CompareTo(WindowArea(right)));
        return result.Count > MaxChildWindows ? result.GetRange(0, MaxChildWindows) : result;
    }

    private static List<IntPtr> EnumerateChildWindows(IntPtr hwnd)
    {
        var children = new List<IntPtr>();

        EnumChildWindows(hwnd, (child, _) =>
        {
            if (IsWindowVisible(child) && WindowArea(child) > 0)
                children.Add(child);

            return true;
        }, IntPtr.Zero);

        return children;
    }

    private static long WindowArea(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var rect)
            ? Math.Max(0L, (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top))
            : 0L;

    private static bool ContainsWindowPoint(IntPtr hwnd, PixelPoint point) =>
        GetWindowRect(hwnd, out var rect) &&
        point.X >= rect.Left && point.X < rect.Right &&
        point.Y >= rect.Top && point.Y < rect.Bottom;

    private Node? BuildRoot(IntPtr hwnd)
    {
        if (_automation is null || _request is null)
            return null;

        try
        {
            var element = _automation.ElementFromHandleBuildCache(hwnd, _request);
            return element is null ? null : CreateNode(element, parent: null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取回这一层子节点（带缓存属性），每个节点一辈子只展开一次。</summary>
    private bool TryExpand(Node node)
    {
        if (node.Children is not null)
            return true;

        if (node.ExpandFailed || node.Element is null || _request is null)
            return false;

        try
        {
            // 一次调用同时刷新本节点缓存并带回子节点集合
            var updated = node.Element.BuildUpdatedCache(_request);
            var array = updated.GetCachedChildren();
            var children = new List<Node>(array?.Length ?? 0);

            if (array is not null)
            {
                var count = Math.Min(array.Length, MaxChildrenPerNode);

                for (var index = 0; index < count; index++)
                {
                    var element = array.GetElement(index);
                    if (element is null)
                        continue;

                    var child = CreateNode(element, node);
                    if (child is not null)
                        children.Add(child);
                }
            }

            node.Children = children;
            return true;
        }
        catch
        {
            // 偶发超时/应用忙可以在下一次探测重试；连续失败才放弃这个分支
            node.Failures++;

            if (node.Failures >= 2)
                node.ExpandFailed = true;

            return false;
        }
    }

    private static Node? CreateNode(IUIAutomationElement element, Node? parent)
    {
        if (element.CachedIsOffscreen != 0)
            return null;

        var rect = element.CachedBoundingRectangle;
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width < 2 || height < 2)
            return null;

        var controlType = element.CachedControlType;

        return new Node(
            new PixelRect(rect.left, rect.top, width, height),
            controlType,
            SafeName(element),
            element,
            parent);
    }

    private static string? SafeName(IUIAutomationElement element)
    {
        try
        {
            var name = element.CachedName;
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

    /// <summary>从最深处往上找第一个「值得框」的节点。</summary>
    private static UiElementCandidate? Pick(Node current, Node root)
    {
        var windowArea = Math.Max(1L, (long)root.Bounds.Width * root.Bounds.Height);
        var visited = 0;

        for (var node = current; node is not null && !ReferenceEquals(node, root); node = node.Parent)
        {
            if (++visited > MaxRectangles)
                break;

            if (node.Bounds.Equals(root.Bounds))
                continue;

            // 无名布局容器只用来继续往上找，不当作可框目标
            if (node.Structural && node.Name is null)
                continue;

            var area = (long)node.Bounds.Width * node.Bounds.Height;
            if (area >= windowArea * 95 / 100)
                return null;

            return new UiElementCandidate(
                node.Bounds,
                node.Name ?? ControlTypeName(node.ControlType),
                ControlTypeName(node.ControlType));
        }

        return null;
    }

    private static bool Contains(PixelRect bounds, PixelPoint point) =>
        point.X >= bounds.X && point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;

    private static bool IsStructural(int controlType) =>
        controlType is UIA_ControlTypeIds.UIA_PaneControlTypeId
            or UIA_ControlTypeIds.UIA_GroupControlTypeId
            or UIA_ControlTypeIds.UIA_CustomControlTypeId;

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

    /// <summary>懒展开的一层控件节点。</summary>
    private sealed class Node(
        PixelRect bounds,
        int controlType,
        string? name,
        IUIAutomationElement element,
        Node? parent)
    {
        public PixelRect Bounds { get; } = bounds;
        public int ControlType { get; } = controlType;
        public string? Name { get; } = name;
        public IUIAutomationElement Element { get; } = element;
        public Node? Parent { get; } = parent;
        public List<Node>? Children { get; set; }
        public bool ExpandFailed { get; set; }
        public int Failures { get; set; }
        public bool Structural => IsStructural(ControlType);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
