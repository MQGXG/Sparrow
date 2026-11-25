// UiElementProbe 分部类：探针流程、缓存发布与树释放。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

public sealed partial class UiElementProbe
{
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
        // 扁平树与 _trees 已解耦：_trees 为空时也可能还有扁平元素需要释放。
        if (_trees.Count == 0 && _flatTrees.Count == 0)
            return;

        foreach (var root in _trees.Values)
            ReleaseTree(root);

        foreach (var flatTree in _flatTrees.Values)
        {
            foreach (var element in flatTree.Elements)
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
        // 窗口矩形和扁平元素存在一起，避免多个浏览器预建时被 EvictOtherTrees 误伤。
        if (_flatTrees.TryGetValue(hwnd, out var flatTree))
        {
            var flatResult = FlatProbe(flatTree.Elements, flatTree.Bounds, point, windowTitle);
            SelectorTrace.Write(
                $"flat hit hwnd=0x{hwnd.ToInt64():X} point={point.X},{point.Y} " +
                $"flat={flatTree.Elements.Count} root={flatTree.Bounds} -> {DescribePath(flatResult.Path)}");
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
}
