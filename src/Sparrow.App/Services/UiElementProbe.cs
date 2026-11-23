using System.Collections.Concurrent;
using System.Diagnostics;
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
/// 一次探测的结果。Completed=false 表示「预算用完、还没走到底」，
/// 调用方可以在光标停住后用更长的预算再细化一次。
/// </summary>
public readonly record struct UiElementProbeResult(
    UiElementCandidate? Candidate,
    bool Completed);

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

    /// <summary>子节点超过这个数才建空间索引（和 snow-shot 的阈值一致）。</summary>
    private const int LinearChildLimit = 16;

    /// <summary>单个窗口最多保留多少懒展开节点，防止异常大树把内存撑爆。</summary>
    private const int MaxNodesPerWindow = 12000;

    /// <summary>单次跨进程调用最多等多久（毫秒）。实际值按剩余预算动态收紧。</summary>
    private const int CallLimitMilliseconds = 400;

    /// <summary>悬停探测的预算：和 snow-shot 一致，170ms 内必须给出结果。</summary>
    public static readonly TimeSpan ForegroundBudget = TimeSpan.FromMilliseconds(170);

    /// <summary>光标停住后的细化预算：和 snow-shot 的 1.5s 对齐，复杂窗口也够走完。</summary>
    public static readonly TimeSpan RefinementBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>遍历途中发布粗框的间隔，让框先出来再收紧。</summary>
    private const int PublicationIntervalMilliseconds = 40;

    /// <summary>容器节点连续拿到几次空子树后放弃（多半是应用还没建好树）。</summary>
    private const int MaxEmptyExpansions = 5;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Dictionary<IntPtr, Node> _trees = [];
    private readonly Dictionary<IntPtr, List<IntPtr>> _childWindows = [];

    private IUIAutomation2? _automation;
    private IUIAutomationCacheRequest? _request;
    private int _nodeCount;
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

    /// <summary>
    /// 在专用线程上探测，永不抛异常。
    /// <paramref name="budget"/> 是总预算：到点就带着「目前走到的最深节点」返回，
    /// 由调用方决定要不要再用更长的预算细化。
    /// <paramref name="preview"/> 会在遍历途中收到越来越小的粗框（约 40ms 一帧）。
    /// </summary>
    public Task<UiElementProbeResult> ProbeAsync(
        IntPtr hwnd,
        PixelPoint point,
        TimeSpan? budget = null,
        IProgress<UiElementCandidate>? preview = null,
        Func<bool>? stillWanted = null)
    {
        if (_disposed)
            return Task.FromResult(new UiElementProbeResult(null, true));

        var gate = stillWanted ?? AlwaysWanted;
        var completion = new TaskCompletionSource<UiElementProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                var result = new UiElementProbeResult(null, true);

                try
                {
                    // 排队期间鼠标已经移开：连根节点都不用查
                    if (gate())
                    {
                        var state = new ProbeState(
                            Stopwatch.StartNew(),
                            budget ?? ForegroundBudget,
                            preview);

                        result = Probe(hwnd, point, gate, state);
                    }
                }
                catch
                {
                    // 目标应用没有 UIA 树、COM 失败、窗口已关闭等：一律退回窗口级
                }

                completion.TrySetResult(result);
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(new UiElementProbeResult(null, true));
        }

        return completion.Task;
    }

    /// <summary>一次探测的预算、计时和「粗框」发布节流。</summary>
    private sealed class ProbeState(Stopwatch watch, TimeSpan budget, IProgress<UiElementCandidate>? preview)
    {
        public TimeSpan Remaining => budget - watch.Elapsed;

        public bool Expired => Remaining <= TimeSpan.Zero;

        public IProgress<UiElementCandidate>? Preview { get; } = preview;

        public TimeSpan LastPublished { get; set; }

        /// <summary>本次探测是否已经为超时重试过一次。</summary>
        public bool RetryUsed { get; set; }

        public void MaybePublish(Node current, Node root)
        {
            if (Preview is null || watch.Elapsed - LastPublished <
                TimeSpan.FromMilliseconds(PublicationIntervalMilliseconds))
            {
                return;
            }

            var candidate = Pick(current, root, resolveName: false, preview: true);
            if (candidate is null)
                return;

            LastPublished = watch.Elapsed;

            try
            {
                Preview.Report(candidate);
            }
            catch
            {
                // UI 已关闭时忽略
            }
        }
    }

    private static readonly Func<bool> AlwaysWanted = static () => true;

    /// <summary>
    /// 提前丢掉缓存的控件树，保留 UIA 的 COM 对象（下次探测不用重新初始化）。
    /// 选区确定、进入 OCR 之后不再需要祖先树，早点把上千个 COM 节点还给系统。
    /// COM 对象必须在 UIA 线程释放，所以投递到队列而不是在调用线程直接清。
    /// </summary>
    public void ReleaseTrees()
    {
        if (_disposed)
            return;

        try
        {
            _queue.Add(ReleaseCachedTrees);
        }
        catch (InvalidOperationException)
        {
            // 已 Dispose：队列不再接收，树会随 Release() 一起清理
        }
    }

    private void ReleaseCachedTrees()
    {
        if (_trees.Count == 0)
            return;

        foreach (var root in _trees.Values)
            ReleaseTree(root);

        _trees.Clear();
        _childWindows.Clear();
        _nodeCount = 0;
    }

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
        automation.ConnectionTimeout = (uint)CallLimitMilliseconds;
        automation.TransactionTimeout = (uint)CallLimitMilliseconds;

        var request = automation.CreateCacheRequest();
        request.TreeScope = TreeScope.TreeScope_Element | TreeScope.TreeScope_Children;
        // ControlView 会去掉纯布局的中间层；Chromium 那种冗余容器交给结构回溯处理
        request.TreeFilter = automation.ControlViewCondition;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        request.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        // 故意不缓存 Name：Chromium 上每个节点的名字都可能触发跨进程文本提取，
        // 是首屏探测最大的开销。只对最终选中的节点按需取一次。

        _automation = automation;
        _request = request;
    }

    private void Release()
    {
        ReleaseCachedTrees();

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

    private UiElementProbeResult Probe(
        IntPtr hwnd,
        PixelPoint point,
        Func<bool> stillWanted,
        ProbeState state)
    {
        if (_request is null)
            return new UiElementProbeResult(null, true);

        var outcome = ProbeWindow(hwnd, point, stillWanted, state);
        if (outcome.Candidate is not null || !outcome.Completed)
            return outcome;

        // UWP/WinUI 的顶层是 ApplicationFrameWindow，UIA 树里只有窗框；
        // 真正的内容在 Windows.UI.Core.CoreWindow 这类子窗口里
        foreach (var child in ChildWindowsAt(hwnd, point))
        {
            if (!stillWanted() || state.Expired)
                return new UiElementProbeResult(null, false);

            outcome = ProbeWindow(child, point, stillWanted, state);
            if (outcome.Candidate is not null || !outcome.Completed)
                return outcome;
        }

        return new UiElementProbeResult(null, true);
    }

    private UiElementProbeResult ProbeWindow(
        IntPtr hwnd,
        PixelPoint point,
        Func<bool> stillWanted,
        ProbeState state)
    {
        if (!_trees.TryGetValue(hwnd, out var root))
        {
            // 只保留当前窗口的控件树：一次截图里通常只需要一个窗口，
            // 跨窗口累积会把工作集推到几百 MB
            EvictOtherTrees(hwnd);

            root = BuildRoot(hwnd, state.Remaining);
            if (root is null)
                return new UiElementProbeResult(null, !state.Expired);

            _trees[hwnd] = root;
        }

        if (!Contains(root.Bounds, point))
            return new UiElementProbeResult(null, true);

        var current = root;

        for (var step = 0; step < MaxSteps; step++)
        {
            // 长时间遍历期间鼠标可能已经移开，及时收手
            if (!stillWanted())
                return new UiElementProbeResult(null, true);

            // 预算到点：把已经走到的最深节点先交出去，调用方可以再细化
            if (state.Expired)
            {
                return new UiElementProbeResult(
                    Pick(current, root, resolveName: false, preview: true), false);
            }

            var expanded = TryExpand(current, state);

            // 超时 / 空树在本次探测内重试一次；和 snow-shot 的 refinement retry 一致
            if (!expanded && !state.RetryUsed && !current.ExpandFailed && !state.Expired)
            {
                state.RetryUsed = true;
                expanded = TryExpand(current, state);
            }

            if (!expanded)
            {
                // 可重试的失败（预算/超时/空树）留给细化；彻底失败就收手
                return new UiElementProbeResult(
                    Pick(current, root, resolveName: true, preview: false),
                    current.ExpandFailed);
            }

            var child = HitTest(current, point);

            if (child is null)
            {
                // UIA 的兄弟顺序不保证堆叠顺序：Chromium 这类应用会用等大的
                // Pane/Group 盖住真正的内容分支，需要沿等大结构节点回溯
                var alternative = StructuralAlternative(current, point);
                if (alternative is null)
                {
                    return new UiElementProbeResult(
                        Pick(current, root, resolveName: true, preview: false), true);
                }

                current = alternative;
                continue;
            }

            current = child;
            state.MaybePublish(current, root);
        }

        return new UiElementProbeResult(
            Pick(current, root, resolveName: true, preview: false), true);
    }

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
        _childWindows.Clear();

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

    private Node? BuildRoot(IntPtr hwnd, TimeSpan remaining)
    {
        if (_automation is null || _request is null)
            return null;

        ApplyTimeouts(remaining);

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

    /// <summary>
    /// 每次跨进程调用前把 UIA 超时收在剩余预算内：
    /// 单次调用最多 400ms，绝不把整次探测拖到卡住。
    /// </summary>
    private void ApplyTimeouts(TimeSpan remaining)
    {
        if (_automation is null)
            return;

        var milliseconds = (uint)Math.Clamp(
            remaining.TotalMilliseconds, 1, CallLimitMilliseconds);

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

    /// <summary>取回这一层子节点（带缓存属性），每个节点一辈子只展开一次。</summary>
    private bool TryExpand(Node node, ProbeState state)
    {
        if (node.Children is not null)
            return true;

        if (node.ExpandFailed || node.Element is null || _request is null)
            return false;

        // 预算已经用完：这次先不调跨进程，留给细化
        if (state.Expired)
            return false;

        // 异常大树到上限就不再往下展开，宁可退回窗口级也不撑爆内存
        if (_nodeCount >= MaxNodesPerWindow)
            return false;

        try
        {
            // 第一次展开：把整批子节点数组缓存下来，等会儿逐个解码
            if (node.PendingChildren is null)
            {
                ApplyTimeouts(state.Remaining);

                // 一次调用同时刷新本节点缓存并带回子节点集合
                var updated = node.Element.BuildUpdatedCache(_request);
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
                    return false;

                if (_nodeCount >= MaxNodesPerWindow)
                    break;

                var element = array!.GetElement(node.PendingIndex);
                node.PendingIndex++;

                if (element is null)
                    continue;

                var child = CreateNode(element, node);
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
                    node.ExpandFailed = true;

                return false;
            }

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

    private Node? CreateNode(IUIAutomationElement element, Node? parent)
    {
        if (element.CachedIsOffscreen != 0)
            return null;

        var rect = element.CachedBoundingRectangle;
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width < 2 || height < 2)
            return null;

        var controlType = element.CachedControlType;

        _nodeCount++;

        return new Node(
            new PixelRect(rect.left, rect.top, width, height),
            controlType,
            element,
            parent);
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
    /// 从最深处往上找「值得框」的节点。
    /// <paramref name="preview"/> 为 true 时连结构容器也直接返回——粗框阶段要让框
    /// 每下一层就收紧一次，不能等到非结构控件才出框（VS 这类窗口前几层全是 Pane）。
    /// 最终结果才跳过结构容器，并最多为两个结构节点读名字。
    /// </summary>
    private static UiElementCandidate? Pick(
        Node current,
        Node root,
        bool resolveName,
        bool preview)
    {
        var windowArea = Math.Max(1L, (long)root.Bounds.Width * root.Bounds.Height);
        var visited = 0;
        var structuralChecked = 0;

        for (var node = current; node is not null && !ReferenceEquals(node, root); node = node.Parent)
        {
            if (++visited > MaxRectangles)
                break;

            if (node.Bounds.Equals(root.Bounds))
                continue;

            var area = (long)node.Bounds.Width * node.Bounds.Height;
            if (area >= windowArea * 95 / 100)
                return null;

            var kind = ControlTypeName(node.ControlType);

            if (!node.Structural)
            {
                return new UiElementCandidate(
                    node.Bounds,
                    resolveName ? SafeName(node.Element) ?? kind : kind,
                    kind);
            }

            if (preview)
                return new UiElementCandidate(node.Bounds, kind, kind);

            // 结构容器只给最深的一个查名字；再往上通常是整块窗格，不值得跨进程
            if (resolveName && structuralChecked++ < 1)
            {
                var name = SafeName(node.Element);
                if (name is not null)
                    return new UiElementCandidate(node.Bounds, name, kind);
            }
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

    /// <summary>可能装子控件的容器类型：空子树先当「树还没建好」处理。</summary>
    private static bool IsContainer(int controlType) =>
        IsStructural(controlType)
        || controlType is UIA_ControlTypeIds.UIA_WindowControlTypeId
            or UIA_ControlTypeIds.UIA_DocumentControlTypeId
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

    /// <summary>懒展开的一层控件节点。</summary>
    private sealed class Node(
        PixelRect bounds,
        int controlType,
        IUIAutomationElement element,
        Node? parent)
    {
        public PixelRect Bounds { get; } = bounds;
        public int ControlType { get; } = controlType;
        public IUIAutomationElement Element { get; } = element;
        public Node? Parent { get; } = parent;
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
