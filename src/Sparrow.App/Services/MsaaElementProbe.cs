using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

namespace Sparrow.App.Services;

/// <summary>
/// MSAA（IAccessible）兜底探测。
///
/// 只在 UIA 没命中控件时调用：Electron / 老 Win32 应用经常把树暴露在 MSAA 里，
/// UIA 那边只剩一个整窗。用法和 <see cref="UiElementProbe"/> 一样，返回同一个候选类型。
///
/// MSAA 没有超时机制：某个应用的 provider 卡死会把调用线程一起拖住。
/// 所以探测跑在独立线程上，超时就记下这个窗口不再重试，并换一个新的工作线程继续服务。
/// </summary>
public sealed class MsaaElementProbe : IDisposable
{
    private const uint ObjIdClient = 0xFFFFFFFC;
    private const uint ObjIdWindow = 0x00000000;
    private const int ChildIdSelf = 0;

    /// <summary>MSAA 下钻的最大层数（snow-shot 是 64），防止异常树把预算耗光。</summary>
    private const int MaxDescendDepth = 64;

    /// <summary>路径最多保留多少层矩形（snow-shot 是 100）。</summary>
    private const int MaxPathRects = 100;

    private const int StateSystemInvisible = 0x00008000;
    private const int StateSystemOffscreen = 0x00010000;

    /// <summary>最多放弃几个卡死的线程；再多就彻底停用 MSAA，避免线程泄漏。</summary>
    private const int MaxAbandonedWorkers = 2;

    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private static readonly Func<bool> AlwaysWanted = static () => true;

    private static readonly bool DiagnosticsEnabled = Debugger.IsAttached
        || Environment.GetEnvironmentVariable("SPARROW_SELECTOR_DIAGNOSTICS") == "1";

    private static void Log(string message)
    {
        if (DiagnosticsEnabled)
            Debug.WriteLine($"[Sparrow.Selector] msaa {message}");
    }

    private static string DescribePath(IReadOnlyList<UiElementCandidate> path) =>
        string.Join(" > ", path.Select(candidate =>
            $"{candidate.ControlType}:{candidate.Bounds.Width}x{candidate.Bounds.Height}"));

    private readonly BlockingCollection<Action> _queue = new();
    private readonly HashSet<IntPtr> _unresponsive = [];
    private readonly object _gate = new();

    private volatile bool _disposed;
    private volatile bool _disabled;
    private int _abandoned;

    public MsaaElementProbe() => StartWorker();

    /// <summary>
    /// 在独立线程上做 MSAA 命中，超时返回空路径并把这个窗口加入黑名单。
    /// <paramref name="budget"/> 同时是「调用卡死」的上限。
    /// </summary>
    public async Task<UiElementProbeResult> ProbeAsync(
        IntPtr hwnd,
        PixelPoint point,
        TimeSpan budget,
        Func<bool>? stillWanted = null,
        string? windowTitle = null)
    {
        if (_disposed || _disabled || IsUnresponsive(hwnd) || IsHungAppWindow(hwnd))
            return new UiElementProbeResult([], UiElementStopReason.ProviderFailure);

        var wanted = stillWanted ?? AlwaysWanted;
        var completion = new TaskCompletionSource<UiElementProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = Environment.TickCount64 + (long)Math.Max(1, budget.TotalMilliseconds);

        try
        {
            _queue.Add(() =>
            {
                var result = new UiElementProbeResult([], UiElementStopReason.ProviderFailure);

                try
                {
                    if (wanted())
                        result = Probe(hwnd, point, wanted, deadline, windowTitle);
                }
                catch
                {
                    // 没有 MSAA 树、COM 失败、窗口已关闭：都按「没命中」处理
                }

                Log($"hwnd=0x{hwnd.ToInt64():X} " +
                    $"path=[{DescribePath(result.Path)}] reason={result.Reason}");

                SelectorTrace.Write(
                    $"msaa hwnd=0x{hwnd.ToInt64():X} path=[{DescribePath(result.Path)}] " +
                    $"reason={result.Reason}");

                completion.TrySetResult(result);
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(new UiElementProbeResult([], UiElementStopReason.ProviderFailure));
        }

        try
        {
            return await completion.Task.WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            // provider 卡死：这个窗口不再试，换一个新线程继续服务其它窗口
            MarkUnresponsive(hwnd);

            var abandoned = Interlocked.Increment(ref _abandoned);

            if (abandoned <= MaxAbandonedWorkers)
                StartWorker();
            else
                _disabled = true;   // 放弃太多线程就彻底停用，避免线程泄漏

            return new UiElementProbeResult([], UiElementStopReason.ProviderTimeout);
        }
    }

    private void StartWorker()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Sparrow.MSAA.{_abandoned}"
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }
        catch
        {
            // 队列关闭 / 线程被放弃
        }
    }

    private bool IsUnresponsive(IntPtr hwnd)
    {
        lock (_gate)
            return _unresponsive.Contains(hwnd);
    }

    private void MarkUnresponsive(IntPtr hwnd)
    {
        lock (_gate)
            _unresponsive.Add(hwnd);
    }

    private static UiElementProbeResult Probe(
        IntPtr hwnd,
        PixelPoint point,
        Func<bool> wanted,
        long deadline,
        string? windowTitle)
    {
        if (!GetWindowRect(hwnd, out var windowRect))
            return new UiElementProbeResult([], UiElementStopReason.ProviderFailure);

        var windowBounds = new PixelRect(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);

        if (windowBounds.Width < 2 || windowBounds.Height < 2)
            return new UiElementProbeResult([], UiElementStopReason.ProviderFailure);

        // 和 snow-shot 一致先取 OBJID_WINDOW：Chrome 的 OBJID_CLIENT 经常只给
        // 一个无用的 client，先试它会把 OBJID_WINDOW 挡掉。
        var root = AccessibleFromWindow(hwnd, ObjIdWindow)
                   ?? AccessibleFromWindow(hwnd, ObjIdClient);

        if (root is null)
            return new UiElementProbeResult([], UiElementStopReason.ProviderFailure);

        // 下钻会顺路拿到新的 IDispatch，全部集中释放
        var created = new List<object> { root };

        try
        {
            var (steps, reason) = BuildHitPath(
                root, windowBounds, point, wanted, deadline, created);

            // OBJID_WINDOW 只给到窗口时，再试 OBJID_CLIENT（UWP/部分自绘应用）
            if (steps.Count <= 1)
            {
                var clientRoot = AccessibleFromWindow(hwnd, ObjIdClient);

                if (clientRoot is not null && !SameAccessible(clientRoot, root))
                {
                    created.Add(clientRoot);

                    var (clientSteps, clientReason) = BuildHitPath(
                        clientRoot, windowBounds, point, wanted, deadline, created);

                    if (clientSteps.Count > steps.Count)
                    {
                        steps = clientSteps;
                        reason = clientReason;
                    }
                }
            }

            if (steps.Count == 0)
                return new UiElementProbeResult([], reason);

            // MSAA 只给到窗口（老 Win32 把按钮做成子 HWND）：用可见子窗口矩形补层，
            // 和 snow-shot 的 merge_hit_paths 一致。
            steps = MergeFallbackRects(steps, hwnd, windowBounds, point, root);

            var path = new List<UiElementCandidate>(steps.Count + 1);

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var kind = RoleName(step.Role);
                var title = kind;

                // 只给最深一层查名字；MSAA 的 accName 也可能触发跨进程文本提取
                if (i == 0 && step.ChildId >= 0)
                    title = TryGetName(step.Node, step.ChildId) ?? kind;

                // 路径末项就是窗口：统一用调用方给的窗口标题
                if (i == steps.Count - 1 && step.Bounds.Equals(windowBounds))
                    title = string.IsNullOrWhiteSpace(windowTitle) ? "窗口" : windowTitle;

                path.Add(new UiElementCandidate(step.Bounds, title, kind));
            }

            if (path.Count == 0 || !path[^1].Bounds.Equals(windowBounds))
            {
                path.Add(new UiElementCandidate(
                    windowBounds,
                    string.IsNullOrWhiteSpace(windowTitle) ? "窗口" : windowTitle,
                    "窗口"));
            }

            return new UiElementProbeResult(path, reason);
        }
        finally
        {
            foreach (var comObject in created)
                Release(comObject);
        }
    }

    private readonly record struct HitStep(
        PixelRect Bounds,
        int Role,
        IAccessible Node,
        int ChildId);

    /// <summary>
    /// 从窗口根开始，沿 accHitTest / accChild 往下收集完整路径（深 → 浅），
    /// 每层裁剪到窗口、跳过不可见/脱屏/重复的矩形。形状对齐 Snow Shot 的 MSAA 路径。
    /// </summary>
    private static (List<HitStep> Steps, UiElementStopReason Reason) BuildHitPath(
        IAccessible root,
        PixelRect windowBounds,
        PixelPoint point,
        Func<bool> wanted,
        long deadline,
        List<object> created)
    {
        var steps = new List<HitStep>(8);
        var node = root;
        var childId = ChildIdSelf;
        var reason = UiElementStopReason.Complete;
        var depth = 0;

        for (; depth < MaxDescendDepth; depth++)
        {
            if (!wanted())
            {
                reason = UiElementStopReason.Cancelled;
                break;
            }

            if (Environment.TickCount64 >= deadline)
            {
                reason = UiElementStopReason.BudgetExhausted;
                break;
            }

            AddStep(steps, node, childId, windowBounds, point);

            object? result;

            try
            {
                result = node.accHitTest(point.X, point.Y);
            }
            catch
            {
                break;
            }

            // VT_I4：命中当前节点的某个子元素（<=0 是 self/无效）
            if (result is int hitChildId)
            {
                if (hitChildId <= ChildIdSelf)
                    break;

                if (TryGetChildObject(node, hitChildId) is { } child)
                {
                    created.Add(child);
                    node = child;
                    childId = ChildIdSelf;
                    continue;
                }

                // 简单子元素拿不到 IDispatch：补一层矩形后结束
                AddStep(steps, node, hitChildId, windowBounds, point);
                break;
            }

            if (AsAccessible(result) is { } hit)
            {
                if (SameAccessible(hit, node))
                    break;

                created.Add(hit);
                node = hit;
                childId = ChildIdSelf;
                continue;
            }

            break;
        }

        if (reason == UiElementStopReason.Complete && depth >= MaxDescendDepth)
            reason = UiElementStopReason.TraversalLimit;

        // 收集顺序是浅 → 深；翻转成与 UIA 一致的深 → 浅
        steps.Reverse();

        // 末项固定为窗口；模型和界面都以「最后一项是窗口」为前提
        if (steps.Count == 0 || !steps[^1].Bounds.Equals(windowBounds))
        {
            AddStep(steps, root, ChildIdSelf, windowBounds, point);

            if (steps.Count == 0 || !steps[^1].Bounds.Equals(windowBounds))
                steps.Add(new HitStep(windowBounds, RoleSystemWindow, root, ChildIdSelf));
        }

        return (steps, reason);
    }

    private static void AddStep(
        List<HitStep> steps,
        IAccessible node,
        int childId,
        PixelRect windowBounds,
        PixelPoint point)
    {
        if (steps.Count >= MaxPathRects)
            return;

        if ((TryGetState(node, childId) &
             (StateSystemInvisible | StateSystemOffscreen)) != 0)
        {
            return;
        }

        if (!TryGetBounds(node, childId, out var bounds))
            return;

        var clipped = Intersect(bounds, windowBounds);

        if (clipped.Width < 2 || clipped.Height < 2 || !Contains(clipped, point))
            return;

        if (steps.Count > 0 && steps[^1].Bounds.Equals(clipped))
            return;

        steps.Add(new HitStep(clipped, TryGetRole(node, childId), node, childId));
    }

    private static PixelRect Intersect(PixelRect left, PixelRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);

        return rightEdge <= x || bottomEdge <= y
            ? default
            : new PixelRect(x, y, rightEdge - x, bottomEdge - y);
    }

    /// <summary>
    /// 可见子窗口矩形（小 → 大）。老 Win32 应用把控件做成子 HWND，
    /// MSAA 树只到 client 时靠这层兜底；Snow Shot 的 MSAA 也用同一招。
    /// </summary>
    private static List<PixelRect> ChildWindowRects(
        IntPtr hwnd, PixelRect windowBounds, PixelPoint point)
    {
        var rects = new List<PixelRect>(8);

        EnumChildWindows(hwnd, (child, _) =>
        {
            if (rects.Count >= 16)
                return false;

            if (!IsWindowVisible(child) || !GetWindowRect(child, out var rect))
                return true;

            var bounds = new PixelRect(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
            var clipped = Intersect(bounds, windowBounds);

            if (clipped.Width < 2 || clipped.Height < 2 || !Contains(clipped, point))
                return true;

            if (!rects.Any(existing => existing.Equals(clipped)))
                rects.Add(clipped);

            return true;
        }, IntPtr.Zero);

        rects.Sort((left, right) =>
            ((long)left.Width * left.Height).CompareTo((long)right.Width * right.Height));

        return rects;
    }

    /// <summary>
    /// 把 MSAA 路径和可见子窗口矩形合并成一条「小 → 大」的包含链，
    /// 对齐 snow-shot 的 merge_hit_paths：MSAA 只到客户端时，子窗口/文档层仍可选。
    /// </summary>
    private static List<HitStep> MergeFallbackRects(
        List<HitStep> steps,
        IntPtr hwnd,
        PixelRect windowBounds,
        PixelPoint point,
        IAccessible root)
    {
        var candidates = new List<HitStep>(steps);

        foreach (var rect in ChildWindowRects(hwnd, windowBounds, point))
            candidates.Add(new HitStep(rect, RoleSystemClient, root, -1));

        if (candidates.Count == 0)
            return steps;

        var seen = new HashSet<(int X, int Y, int Width, int Height)>();
        var sorted = candidates
            .Where(candidate => candidate.Bounds.Width > 0
                                && candidate.Bounds.Height > 0
                                && seen.Add((
                                    candidate.Bounds.X,
                                    candidate.Bounds.Y,
                                    candidate.Bounds.Width,
                                    candidate.Bounds.Height)))
            .OrderBy(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .ToList();

        var seed = sorted.FirstOrDefault(candidate => Contains(candidate.Bounds, point));

        if (seed.Bounds.Width <= 0)
            return steps;

        var path = new List<HitStep> { seed };

        foreach (var candidate in sorted)
        {
            var current = path[^1];

            if (candidate.Bounds.Equals(current.Bounds))
                continue;

            if (ContainsRect(candidate.Bounds, current.Bounds))
                path.Add(candidate);
        }

        if (!path[^1].Bounds.Equals(windowBounds))
            path.Add(new HitStep(windowBounds, RoleSystemWindow, root, ChildIdSelf));

        return path;
    }

    private static bool ContainsRect(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X &&
        inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width &&
        inner.Y + inner.Height <= outer.Y + outer.Height;

    private static IAccessible? TryGetChildObject(IAccessible node, int childId)
    {
        try
        {
            return node.accChild(childId) as IAccessible;
        }
        catch
        {
            return null;
        }
    }

    private static IAccessible? AsAccessible(object? value)
    {
        if (value is IAccessible accessible)
            return accessible;

        if (value is null || !Marshal.IsComObject(value))
            return null;

        try
        {
            return (IAccessible)value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>COM 对象身份比较：不同 RCW 可能指向同一个 IAccessible，ReferenceEquals 不可靠。</summary>
    private static bool SameAccessible(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        try
        {
            var leftUnknown = Marshal.GetIUnknownForObject(left);
            var rightUnknown = Marshal.GetIUnknownForObject(right);

            try
            {
                return leftUnknown == rightUnknown;
            }
            finally
            {
                Marshal.Release(leftUnknown);
                Marshal.Release(rightUnknown);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool Contains(PixelRect bounds, PixelPoint point) =>
        point.X >= bounds.X && point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;

    private static bool TryGetBounds(IAccessible node, int childId, out PixelRect bounds)
    {
        bounds = default;

        try
        {
            node.accLocation(
                out var left, out var top, out var width, out var height, childId);

            if (width < 2 || height < 2)
                return false;

            bounds = new PixelRect(left, top, width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int TryGetState(IAccessible node, int childId)
    {
        try
        {
            return ToInt(node.accState(childId));
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetRole(IAccessible node, int childId)
    {
        try
        {
            return ToInt(node.accRole(childId));
        }
        catch
        {
            return 0;
        }
    }

    private static string? TryGetName(IAccessible node, int childId)
    {
        try
        {
            return node.accName(childId) as string;
        }
        catch
        {
            return null;
        }
    }

    private static int ToInt(object? value) => value switch
    {
        int number => number,
        short number => number,
        long number => (int)number,
        _ => 0
    };

    private static IAccessible? AccessibleFromWindow(IntPtr hwnd, uint objectId)
    {
        try
        {
            var iid = IidIAccessible;
            return AccessibleObjectFromWindow(hwnd, objectId, ref iid, out var accessible) == 0
                ? accessible
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Release(object? comObject)
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

    private const int RoleSystemWindow = 0x9;
    private const int RoleSystemClient = 0xA;
    private const int RoleSystemMenuItem = 0xC;
    private const int RoleSystemDocument = 0xF;
    private const int RoleSystemPane = 0x10;
    private const int RoleSystemGrouping = 0x14;
    private const int RoleSystemToolbar = 0x16;
    private const int RoleSystemTable = 0x18;
    private const int RoleSystemCell = 0x1D;
    private const int RoleSystemLink = 0x1E;
    private const int RoleSystemList = 0x21;
    private const int RoleSystemListItem = 0x22;
    private const int RoleSystemOutline = 0x23;
    private const int RoleSystemOutlineItem = 0x24;
    private const int RoleSystemPageTab = 0x25;
    private const int RoleSystemGraphic = 0x28;
    private const int RoleSystemStaticText = 0x29;
    private const int RoleSystemText = 0x2A;
    private const int RoleSystemPushButton = 0x2B;
    private const int RoleSystemCheckButton = 0x2C;
    private const int RoleSystemRadioButton = 0x2D;
    private const int RoleSystemComboBox = 0x2E;
    private const int RoleSystemDropList = 0x2F;
    private const int RoleSystemProgressBar = 0x30;
    private const int RoleSystemSlider = 0x33;
    private const int RoleSystemPageTabList = 0x3C;
    private const int RoleSystemSplitButton = 0x3E;
    private const int RoleSystemApplication = 0xE;

    private static string RoleName(int role) => role switch
    {
        RoleSystemPushButton or RoleSystemSplitButton => "按钮",
        RoleSystemCheckButton => "复选框",
        RoleSystemRadioButton => "单选项",
        RoleSystemComboBox or RoleSystemDropList => "下拉框",
        RoleSystemText => "输入框",
        RoleSystemLink => "链接",
        RoleSystemGraphic => "图片",
        RoleSystemListItem => "列表项",
        RoleSystemMenuItem => "菜单项",
        RoleSystemPageTab => "选项卡",
        RoleSystemPageTabList => "选项卡",
        RoleSystemOutlineItem => "树节点",
        RoleSystemSlider => "滑块",
        RoleSystemProgressBar => "进度条",
        RoleSystemToolbar => "工具栏",
        RoleSystemTable => "表格",
        RoleSystemCell => "单元格",
        RoleSystemDocument => "文档",
        RoleSystemPane => "窗格",
        RoleSystemGrouping => "分组",
        RoleSystemClient => "客户端",
        RoleSystemWindow => "窗口",
        RoleSystemOutline or RoleSystemList => "列表",
        RoleSystemStaticText => "文本",
        RoleSystemApplication => "应用",
        _ => "控件"
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
            // 已关闭
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwObjectId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    /// <summary>只声明用到的 IAccessible 成员；IDispatch 按 DispId 绑定。</summary>
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessible
    {
        [DispId(-5001)]
        int accChildCount { get; }

        [DispId(-5002)]
        object? accChild([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        object? accName([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5006)]
        object? accRole([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5007)]
        object? accState([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5014)]
        void accLocation(
            out int pxLeft,
            out int pyTop,
            out int pcxWidth,
            out int pcyHeight,
            [MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5016)]
        object? accHitTest(int xLeft, int yTop);
    }
}
