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
public sealed partial class UiElementProbe : IDisposable
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

    /// <summary>
    /// 预建 UIA 树时 Descendants 批量请求的调用上限。这是遮罩显示前的一次性成本，
    /// 调试器附加 / 页面首次建树时可能超过前景的 168ms 上限，需要单独放宽并重试。
    /// </summary>
    private const int PrewarmCallLimitMilliseconds = 2000;

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
    private readonly Dictionary<IntPtr, FlatTree> _flatTrees = [];
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
                            var all = FindDescendantsWithRetry(root.Element, state);

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
                                    _flatTrees[hwnd] = new FlatTree(flat, root.Bounds);
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
                        $"prewarm no-flat hwnd=0x{hwnd.ToInt64():X} nodes={_nodeCount} " +
                        $"elapsed={state.Remaining.TotalMilliseconds:0}ms-left");

                    // 没建出扁平树就无法在遮罩期命中行级元素。
                    // 不能返回 true，否则宿主会以为预建成功、跳过实时细化。
                    completion.TrySetResult(false);
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

    /// <summary>
    /// 预建的 Descendants 批量请求：给 2s 专用超时并重试一次。
    /// 调试器附加或页面首次建树较慢时，第一次常会超过前景调用上限而超时；
    /// 直接吞掉异常会得到「预建成功但没有扁平树」，遮罩期只能选到窗格。
    /// </summary>
    private IUIAutomationElementArray? FindDescendantsWithRetry(
        IUIAutomationElement root, ProbeState state)
    {
        var automation = _automation;

        if (automation is null || state.Request is null)
            return null;

        try
        {
            automation.ConnectionTimeout = PrewarmCallLimitMilliseconds;
            automation.TransactionTimeout = PrewarmCallLimitMilliseconds;
        }
        catch
        {
            // 设置超时失败仍按当前值尝试
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return root.FindAllBuildCache(
                    TreeScope.TreeScope_Descendants,
                    automation.ControlViewCondition,
                    state.Request);
            }
            catch (Exception error)
            {
                SelectorTrace.Write(
                    $"prewarm descendants failed attempt={attempt} hresult=0x{error.HResult:X}");

                if (attempt == 0)
                    Thread.Sleep(120);
            }
        }

        return null;
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

}
