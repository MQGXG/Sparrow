using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

/// <summary>UI Automation 探测到的一个控件候选。</summary>
public sealed record UiElementCandidate(
    PixelRect Bounds,
    string Title,
    string ControlType)
{
    public bool Contains(PixelPoint point) =>
        point.X >= Bounds.X && point.X < Bounds.X + Bounds.Width &&
        point.Y >= Bounds.Y && point.Y < Bounds.Y + Bounds.Height;
}

/// <summary>一次探测为什么停下；和 Snow Shot 的 StopReason 对齐。</summary>
public enum UiElementStopReason
{
    Complete,
    BudgetExhausted,
    DecodingPending,
    ProviderTimeout,
    ProviderFailure,
    Cancelled,
    TraversalLimit
}

/// <summary>
/// 一次探测的结果。Path 是「深 → 浅」的命中路径，末项一定是窗口矩形；
/// 只有窗口一项表示没拿到更细的元素。
/// </summary>
public sealed record UiElementProbeResult(
    IReadOnlyList<UiElementCandidate> Path,
    UiElementStopReason Reason)
{
    public static UiElementProbeResult Empty { get; } = new([], UiElementStopReason.Complete);

    public bool HasPath => Path.Count > 0;

    /// <summary>路径最深的一层；没有路径时为 null。</summary>
    public UiElementCandidate? Deepest => Path.Count > 0 ? Path[0] : null;

    /// <summary>预算/解码/超时都可以用更长预算再细化一次。</summary>
    public bool CanRefine => Reason is UiElementStopReason.BudgetExhausted
        or UiElementStopReason.DecodingPending
        or UiElementStopReason.ProviderTimeout;
}

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

    /// <summary>
    /// 单层最多解码多少个子节点。网页长列表 / 大表格常常超过 600 个兄弟，
    /// 截断会让光标下的元素永远命中不到，这是「有时准有时不准」的主要来源。
    /// 这里只做异常保护，正常情况由预算和 MaxNodesPerWindow 约束。
    /// </summary>
    private const int MaxChildrenPerNode = 65536;

    private const int MaxRectangles = 100;
    private const int MaxChildWindows = 10;

    /// <summary>子节点超过这个数才建空间索引（和 snow-shot 的阈值一致）。</summary>
    private const int LinearChildLimit = 16;

    /// <summary>单个窗口最多保留多少懒展开节点，防止异常大树把内存撑爆。</summary>
    private const int MaxNodesPerWindow = 30000;

    /// <summary>悬停单次跨进程调用的上限；和 snow-shot 的 foreground 预算一致。</summary>
    private const int ForegroundCallLimitMilliseconds = 168;

    /// <summary>细化单次跨进程调用的上限；snow-shot 给到 500ms，大页面才走得完。</summary>
    private const int RefinementCallLimitMilliseconds = 500;

    /// <summary>悬停探测的预算：和 snow-shot 一致，170ms 内必须给出结果。</summary>
    public static readonly TimeSpan ForegroundBudget = TimeSpan.FromMilliseconds(170);

    /// <summary>光标停住后的细化预算：和 snow-shot 的 1.5s 对齐，复杂窗口也够走完。</summary>
    public static readonly TimeSpan RefinementBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>遍历途中发布粗框的间隔（snow-shot 的 refinement 是 32ms）。</summary>
    private const int PublicationIntervalMilliseconds = 32;

    /// <summary>容器节点连续拿到几次空子树后放弃（多半是应用还没建好树）。</summary>
    private const int MaxEmptyExpansions = 8;

    private static readonly Guid IidIAccessible =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private const uint ObjIdClient = 0xFFFFFFFC;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Dictionary<IntPtr, Node> _trees = [];
    private readonly Dictionary<IntPtr, List<FlatElement>> _flatTrees = [];
    private readonly HashSet<IntPtr> _accessibilityPinged = [];

    private IUIAutomation2? _automation;
    private IUIAutomationCacheRequest? _request;
    private IUIAutomationCacheRequest? _rawRequest;
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
        IProgress<IReadOnlyList<UiElementCandidate>>? preview = null,
        Func<bool>? stillWanted = null,
        string? windowTitle = null,
        bool refreshTree = false,
        bool rawView = false)
    {
        if (_disposed)
            return Task.FromResult(UiElementProbeResult.Empty);

        var gate = stillWanted ?? AlwaysWanted;
        var completion = new TaskCompletionSource<UiElementProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = rawView ? _rawRequest ?? _request : _request;

        try
        {
            _queue.Add(() =>
            {
                var result = UiElementProbeResult.Empty;

                try
                {
                    // 排队期间鼠标已经移开：连根节点都不用查
                    if (gate())
                    {
                        var state = new ProbeState(
                            Stopwatch.StartNew(),
                            budget ?? ForegroundBudget,
                            preview,
                            request);

                        result = Probe(hwnd, point, gate, state, windowTitle, refreshTree);
                    }
                }
                catch
                {
                    // 目标应用没有 UIA 树、COM 失败、窗口已关闭等：一律退回窗口级
                }

                // 只记录粗结果/失败，避免每次鼠标移动刷屏；具体路径用 tools/SelectorProbe 看
                if (result.Path.Count <= 1 || result.Reason != UiElementStopReason.Complete)
                {
                    Log($"uia hwnd=0x{hwnd.ToInt64():X} " +
                        $"path=[{DescribePath(result.Path)}] reason={result.Reason}");
                }

                SelectorTrace.Write(
                    $"uia hwnd=0x{hwnd.ToInt64():X} budget={(budget ?? ForegroundBudget).TotalMilliseconds:0} " +
                    $"path=[{DescribePath(result.Path)}] reason={result.Reason}");

                completion.TrySetResult(result);
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(UiElementProbeResult.Empty);
        }

        return completion.Task;
    }

    /// <summary>
    /// 在遮罩显示之前预建整棵 UIA 树。Chrome 被遮罩覆盖后会丢掉网页无障碍树，
    /// 之后再查只剩分组/文档；因此浏览器窗口必须先在无遮挡时建好树。
    /// </summary>
    public Task<bool> PrewarmAsync(IntPtr hwnd, TimeSpan budget)
    {
        if (_disposed)
            return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    var state = new ProbeState(
                        Stopwatch.StartNew(), budget, null, _request);

                    if (!_trees.TryGetValue(hwnd, out var root))
                    {
                        EvictOtherTrees(hwnd);
                        PingRenderChildren(hwnd);
                        root = BuildRoot(hwnd, state.Remaining, state.CallLimit, state.Request);

                        if (root is null)
                        {
                            completion.TrySetResult(false);
                            return;
                        }

                        _trees[hwnd] = root;
                    }

                    // 一次 Descendants 批量请求，明确要求完整后代树。
                    // Chrome 收到这个请求后才会构建网页无障碍。
                    try
                    {
                        if (_automation is not null && state.Request is not null)
                        {
                            var all = root.Element.FindAllBuildCache(
                                TreeScope.TreeScope_Descendants,
                                _automation.ControlViewCondition,
                                state.Request);

                            if (all is not null)
                            {
                                SelectorTrace.Write($"prewarm descendants={all.Length}");

                                var flat = new List<FlatElement>(all.Length);

                                for (var index = 0; index < all.Length; index++)
                                {
                                    var element = all.GetElement(index);

                                    if (element is null)
                                        continue;

                                    var rect = element.CachedBoundingRectangle;

                                    if (rect.right <= rect.left || rect.bottom <= rect.top)
                                    {
                                        try
                                        {
                                            rect = element.CurrentBoundingRectangle;
                                        }
                                        catch
                                        {
                                            continue;
                                        }
                                    }
                                    var bounds = Intersect(
                                        rect.left, rect.top, rect.right, rect.bottom, root.Bounds);

                                    if (bounds.Width < 2 || bounds.Height < 2)
                                        continue;

                                    flat.Add(new FlatElement(
                                        bounds, element.CachedControlType, element));
                                }

                                Marshal.FinalReleaseComObject(all);

                                if (flat.Count > 0)
                                {
                                    _flatTrees[hwnd] = flat;
                                    SelectorTrace.Write(
                                        $"prewarm flat={flat.Count} hwnd=0x{hwnd.ToInt64():X}");

                                    completion.TrySetResult(true);
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        SelectorTrace.Write($"prewarm descendants failed=0x{error.HResult:X}");
                    }

                    var pending = new Queue<Node>();
                    pending.Enqueue(root);

                    while (pending.Count > 0
                           && !state.Expired
                           && _nodeCount < MaxNodesPerWindow)
                    {
                        var node = pending.Dequeue();
                        TryExpand(node, state);

                        if (node.Children is null)
                            continue;

                        foreach (var child in node.Children)
                            pending.Enqueue(child);
                    }

                    SelectorTrace.Write(
                        $"prewarm hwnd=0x{hwnd.ToInt64():X} nodes={_nodeCount} " +
                        $"elapsed={state.Remaining.TotalMilliseconds:0}ms-left");

                    completion.TrySetResult(true);
                }
                catch
                {
                    completion.TrySetResult(false);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    /// <summary>一次探测的预算、计时和「粗框」发布节流。</summary>
    private sealed class ProbeState(
        Stopwatch watch,
        TimeSpan budget,
        IProgress<IReadOnlyList<UiElementCandidate>>? preview,
        IUIAutomationCacheRequest? request)
    {
        public IUIAutomationCacheRequest? Request { get; } = request;

        public TimeSpan Remaining => budget - watch.Elapsed;

        public bool Expired => Remaining <= TimeSpan.Zero;

        public IProgress<IReadOnlyList<UiElementCandidate>>? Preview { get; } = preview;

        /// <summary>
        /// 单次跨进程调用的上限。snow-shot 的 foreground 是 168ms、refinement 是 500ms；
        /// 细化用更宽的调用上限，大型浏览器树才不会每次都卡在 400ms。
        /// </summary>
        public TimeSpan CallLimit { get; } = budget >= RefinementBudget
            ? TimeSpan.FromMilliseconds(RefinementCallLimitMilliseconds)
            : TimeSpan.FromMilliseconds(ForegroundCallLimitMilliseconds);

        /// <summary>只有细化才为重试超时多付一次调用；前景不允许拖到两倍预算。</summary>
        public bool CanRetryTimeout { get; } = budget >= RefinementBudget;

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

            var path = BuildPath(current, root, windowTitle: null, resolveName: false);
            if (path.Count == 0)
                return;

            LastPublished = watch.Elapsed;

            try
            {
                Preview.Report(path);
            }
            catch
            {
                // UI 已关闭时忽略
            }
        }
    }

    private static readonly Func<bool> AlwaysWanted = static () => true;

    /// <summary>调试器附加或显式开启时，把探测路径写到 VS 输出窗口，方便定位「为什么只到文档」。</summary>
    private static readonly bool DiagnosticsEnabled = Debugger.IsAttached
        || Environment.GetEnvironmentVariable("SPARROW_SELECTOR_DIAGNOSTICS") == "1";

    private static void Log(string message)
    {
        if (DiagnosticsEnabled)
            Debug.WriteLine($"[Sparrow.Selector] {message}");
    }

    private static string DescribePath(IReadOnlyList<UiElementCandidate> path) =>
        string.Join(" > ", path.Select(candidate =>
            $"{candidate.ControlType}:{candidate.Bounds.Width}x{candidate.Bounds.Height}"));

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

        foreach (var flat in _flatTrees.Values)
        {
            foreach (var element in flat)
                ReleaseCom(element.Element);
        }

        _trees.Clear();
        _flatTrees.Clear();
        _accessibilityPinged.Clear();
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
        automation.ConnectionTimeout = (uint)ForegroundCallLimitMilliseconds;
        automation.TransactionTimeout = (uint)ForegroundCallLimitMilliseconds;

        var request = CreateRequest(automation, automation.ControlViewCondition);
        var rawRequest = CreateRequest(automation, automation.RawViewCondition);

        _automation = automation;
        _request = request;
        _rawRequest = rawRequest;
    }

    private static IUIAutomationCacheRequest CreateRequest(
        IUIAutomation2 automation,
        IUIAutomationCondition condition)
    {
        var request = automation.CreateCacheRequest();
        request.TreeScope = TreeScope.TreeScope_Element | TreeScope.TreeScope_Children;
        request.TreeFilter = condition;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        request.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        // 故意不缓存 Name：Chromium 上每个节点的名字都可能触发跨进程文本提取，
        // 是首屏探测最大的开销。只对最终选中的节点按需取一次。
        return request;
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

        if (_rawRequest is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_rawRequest);
            }
            catch
            {
            }

            _rawRequest = null;
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
        ProbeState state,
        string? windowTitle,
        bool refreshTree)
    {
        if (state.Request is null)
            return UiElementProbeResult.Empty;

        // 预建过扁平树：完全本地命中，不再依赖被遮罩影响的应用 UIA。
        if (_flatTrees.TryGetValue(hwnd, out var flat)
            && _trees.TryGetValue(hwnd, out var cachedRoot))
        {
            var flatResult = FlatProbe(flat, cachedRoot.Bounds, point, windowTitle);
            SelectorTrace.Write(
                $"flat hit hwnd=0x{hwnd.ToInt64():X} point={point.X},{point.Y} " +
                $"flat={flat.Count} root={cachedRoot.Bounds} -> {DescribePath(flatResult.Path)}");
            return flatResult;
        }

        // 细化阶段重建控件树：浏览器的懒建树/空树结果不能跨阶段复用，
        // 否则前景拿到的空树会一直挡住后面的链接/按钮。
        if (refreshTree)
            ReleaseCachedTrees();

        var outcome = ProbeWindow(hwnd, point, stillWanted, state, windowTitle);

        SelectorTrace.Write(
            $"probe hwnd=0x{hwnd.ToInt64():X} top=[{DescribePath(outcome.Path)}] " +
            $"reason={outcome.Reason} expired={state.Expired} coarse={IsCoarseUiaPath(outcome)}");

        // 顶层 UIA 只给到窗口/文档/窗格时，继续尝试包含光标的子窗口：
        // Chrome/Edge 的网页 UIA 树挂在 Chrome_RenderWidgetHostHWND 上，
        // 只查顶层窗口永远只能拿到 Document。即使顶层还有预算没走完，
        // 只要路径是粗层且还有剩余预算，也先试子窗口。
        if (!IsCoarseUiaPath(outcome) || state.Expired)
        {
            SelectorTrace.Write($"probe skip-child expired={state.Expired} coarse={IsCoarseUiaPath(outcome)}");
            return outcome;
        }

        var topBounds = outcome.Path.Count > 0 ? outcome.Path[^1].Bounds : default;
        var best = outcome;
        var children = ChildWindowsAt(hwnd, point);
        SelectorTrace.Write($"probe children={children.Count}");

        foreach (var child in children)
        {
            if (!stillWanted())
                return new UiElementProbeResult([], UiElementStopReason.Cancelled);

            if (state.Expired)
            {
                SelectorTrace.Write("probe child skipped expired");
                return best;
            }

            var childOutcome = ProbeWindow(child, point, stillWanted, state, windowTitle);

            SelectorTrace.Write(
                $"probe child=0x{child.ToInt64():X} path=[{DescribePath(childOutcome.Path)}] " +
                $"reason={childOutcome.Reason} better={IsBetterPath(childOutcome, best)}");

            if (!IsBetterPath(childOutcome, best))
                continue;

            best = AppendTopWindow(childOutcome, topBounds, windowTitle);

            // 子窗口还有预算可以继续细化：交给调用方用更长预算再来一次
            if (childOutcome.CanRefine)
                return best;

            if (!IsCoarseUiaPath(childOutcome))
                break;
        }

        return best;
    }

    private static bool IsCoarseUiaPath(UiElementProbeResult result)
    {
        if (result.Path.Count <= 1)
            return true;

        return result.Path[0].ControlType is "文档" or "窗格" or "分组" or "窗口" or "应用";
    }

    /// <summary>在预建扁平树里做包含命中，返回深 → 浅路径。</summary>
    private static UiElementProbeResult FlatProbe(
        List<FlatElement> flat,
        PixelRect windowBounds,
        PixelPoint point,
        string? windowTitle)
    {
        var containing = flat
            .Where(element => Contains(element.Bounds, point))
            .OrderBy(element => (long)element.Bounds.Width * element.Bounds.Height)
            .ToList();

        var path = new List<UiElementCandidate>(containing.Count + 1);

        foreach (var element in containing)
        {
            if (path.Count > 0 && !ContainsRect(element.Bounds, path[^1].Bounds))
                continue;

            if (path.Count > 0 && path[^1].Bounds.Equals(element.Bounds))
                continue;

            var kind = ControlTypeName(element.ControlType);
            var title = path.Count == 0
                ? SafeName(element.Element) ?? kind
                : kind;

            path.Add(new UiElementCandidate(element.Bounds, title, kind));
        }

        if (path.Count == 0 || !path[^1].Bounds.Equals(windowBounds))
        {
            path.Add(new UiElementCandidate(
                windowBounds,
                string.IsNullOrWhiteSpace(windowTitle) ? "窗口" : windowTitle,
                "窗口"));
        }

        return new UiElementProbeResult(path, UiElementStopReason.Complete);
    }

    private static bool ContainsRect(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X &&
        inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width &&
        inner.Y + inner.Height <= outer.Y + outer.Height;

    private static bool IsBetterPath(UiElementProbeResult candidate, UiElementProbeResult current)
    {
        if (candidate.Path.Count == 0)
            return false;

        if (current.Path.Count == 0)
            return true;

        if (candidate.Path.Count > current.Path.Count)
            return true;

        return PathArea(candidate) < PathArea(current);
    }

    private static long PathArea(UiElementProbeResult result)
    {
        if (result.Path.Count == 0)
            return long.MaxValue;

        var deepest = result.Path[0].Bounds;
        return (long)deepest.Width * deepest.Height;
    }

    private static UiElementProbeResult AppendTopWindow(
        UiElementProbeResult result,
        PixelRect topBounds,
        string? windowTitle)
    {
        if (topBounds.Width <= 0 || topBounds.Height <= 0)
            return result;

        if (result.Path.Count > 0 && result.Path[^1].Bounds.Equals(topBounds))
            return result;

        var path = new List<UiElementCandidate>(result.Path)
        {
            new(
                topBounds,
                string.IsNullOrWhiteSpace(windowTitle) ? "窗口" : windowTitle,
                "窗口")
        };

        return new UiElementProbeResult(path, result.Reason);
    }

    private UiElementProbeResult ProbeWindow(
        IntPtr hwnd,
        PixelPoint point,
        Func<bool> stillWanted,
        ProbeState state,
        string? windowTitle)
    {
        if (!_trees.TryGetValue(hwnd, out var root))
        {
            // 只保留当前窗口的控件树：一次截图里通常只需要一个窗口，
            // 跨窗口累积会把工作集推到几百 MB
            EvictOtherTrees(hwnd);

            root = BuildRoot(hwnd, state.Remaining, state.CallLimit, state.Request);
            if (root is null)
            {
                return new UiElementProbeResult(
                    [],
                    state.Expired ? UiElementStopReason.BudgetExhausted : UiElementStopReason.ProviderFailure);
            }

            _trees[hwnd] = root;
            SelectorTrace.Write(
                $"window hwnd=0x{hwnd.ToInt64():X} root={ControlTypeName(root.ControlType)} " +
                $"{root.Bounds.X},{root.Bounds.Y} {root.Bounds.Width}x{root.Bounds.Height}");
        }

        if (!Contains(root.Bounds, point))
            return new UiElementProbeResult([], UiElementStopReason.Complete);

        var current = root;

        for (var step = 0; step < MaxSteps; step++)
        {
            // 长时间遍历期间鼠标可能已经移开，及时收手
            if (!stillWanted())
                return new UiElementProbeResult([], UiElementStopReason.Cancelled);

            // 预算到点：把已经走到的最深路径先交出去，调用方可以再细化
            if (state.Expired)
            {
                return new UiElementProbeResult(
                    BuildPath(current, root, windowTitle, resolveName: false),
                    UiElementStopReason.BudgetExhausted);
            }

            var outcome = TryExpand(current, state);

            // 超时只在细化阶段重试（snow-shot 的 retry_timeout 只在 refinement 打开）；
            // 空树（Chromium 懒建）前景也允许重试一次，避免整窗停留太久。
            var retryable = outcome switch
            {
                ExpandOutcome.ProviderTimeout => state.CanRetryTimeout,
                ExpandOutcome.DecodingPending => true,
                _ => false
            };

            if (retryable
                && !state.RetryUsed
                && !current.ExpandFailed
                && !state.Expired)
            {
                state.RetryUsed = true;
                outcome = TryExpand(current, state);
            }

            if (outcome is not ExpandOutcome.Expanded)
            {
                var path = BuildPath(current, root, windowTitle, resolveName: true);

                // 可重试的失败（预算/超时/空树）留给细化；彻底失败就收手
                return new UiElementProbeResult(path, ToStopReason(outcome));
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
                        BuildPath(current, root, windowTitle, resolveName: true),
                        UiElementStopReason.Complete);
                }

                current = alternative;
                continue;
            }

            current = child;
            state.MaybePublish(current, root);
        }

        return new UiElementProbeResult(
            BuildPath(current, root, windowTitle, resolveName: true),
            UiElementStopReason.TraversalLimit);
    }

    private static UiElementStopReason ToStopReason(ExpandOutcome outcome) => outcome switch
    {
        ExpandOutcome.BudgetExhausted => UiElementStopReason.BudgetExhausted,
        ExpandOutcome.DecodingPending => UiElementStopReason.DecodingPending,
        ExpandOutcome.ProviderTimeout => UiElementStopReason.ProviderTimeout,
        ExpandOutcome.ProviderFailure => UiElementStopReason.ProviderFailure,
        ExpandOutcome.TraversalLimit => UiElementStopReason.TraversalLimit,
        _ => UiElementStopReason.Complete
    };

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    private static string? GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// 对渲染子窗口做一次 IAccessible 查询。Chrome/Edge 收到这个请求后才会
    /// 真正构建网页无障碍树；否则 UIA 的 ControlView 只有分组/文档。
    /// </summary>
    private static void PingAccessibility(IntPtr hwnd)
    {
        object? accessible = null;

        try
        {
            var iid = IidIAccessible;
            var hr = AccessibleObjectFromWindow(hwnd, ObjIdClient, ref iid, out accessible);

            if (hr != 0 || accessible is null)
                return;

            SelectorTrace.Write($"accessibility ping 0x{hwnd.ToInt64():X}");
        }
        catch
        {
            // 目标不支持 IAccessible：忽略
        }
        finally
        {
            if (accessible is not null && Marshal.IsComObject(accessible))
            {
                try
                {
                    Marshal.FinalReleaseComObject(accessible);
                }
                catch
                {
                }
            }
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint objectId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? accessible);
}
