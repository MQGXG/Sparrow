using System.Collections.Concurrent;
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

    private const int StateSystemInvisible = 0x00008000;
    private const int StateSystemOffscreen = 0x00010000;

    /// <summary>最多放弃几个卡死的线程；再多就彻底停用 MSAA，避免线程泄漏。</summary>
    private const int MaxAbandonedWorkers = 2;

    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private static readonly Func<bool> AlwaysWanted = static () => true;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly HashSet<IntPtr> _unresponsive = [];
    private readonly object _gate = new();

    private volatile bool _disposed;
    private volatile bool _disabled;
    private int _abandoned;

    public MsaaElementProbe() => StartWorker();

    /// <summary>
    /// 在独立线程上做 MSAA 命中，超时返回 null 并把这个窗口加入黑名单。
    /// <paramref name="budget"/> 同时是「调用卡死」的上限。
    /// </summary>
    public async Task<UiElementCandidate?> ProbeAsync(
        IntPtr hwnd,
        PixelPoint point,
        TimeSpan budget,
        Func<bool>? stillWanted = null)
    {
        if (_disposed || _disabled || IsUnresponsive(hwnd) || IsHungAppWindow(hwnd))
            return null;

        var wanted = stillWanted ?? AlwaysWanted;
        var completion = new TaskCompletionSource<UiElementCandidate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                UiElementCandidate? candidate = null;

                try
                {
                    if (wanted())
                        candidate = Probe(hwnd, point, wanted);
                }
                catch
                {
                    // 没有 MSAA 树、COM 失败、窗口已关闭：都按「没命中」处理
                }

                completion.TrySetResult(candidate);
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(null);
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

            return null;
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

    private static UiElementCandidate? Probe(IntPtr hwnd, PixelPoint point, Func<bool> wanted)
    {
        if (!GetWindowRect(hwnd, out var windowRect))
            return null;

        var root = AccessibleFromWindow(hwnd, ObjIdClient)
                   ?? AccessibleFromWindow(hwnd, ObjIdWindow);

        if (root is null)
            return null;

        IAccessible? hitNode = null;

        try
        {
            var hit = HitTest(root, point);
            if (hit is not { } found)
                return null;

            hitNode = ReferenceEquals(found.Node, root) ? null : found.Node;

            if (!wanted())
                return null;

            if (!TryGetBounds(found.Node, found.ChildId, out var bounds))
                return null;

            // 整窗、窗框外、小于 2px 的都不算「控件」
            if (!IsUsable(bounds, windowRect))
                return null;

            if ((TryGetState(found.Node, found.ChildId) &
                 (StateSystemInvisible | StateSystemOffscreen)) != 0)
            {
                return null;
            }

            var role = TryGetRole(found.Node, found.ChildId);
            var kind = RoleName(role);

            // 无名的大容器（窗格/文档/客户端）没有框选价值，交给窗口级候选
            if (IsStructuralRole(role) && TryGetName(found.Node, found.ChildId) is null)
                return null;

            var name = TryGetName(found.Node, found.ChildId);
            return new UiElementCandidate(bounds, name ?? kind, kind);
        }
        finally
        {
            Release(hitNode);
            Release(root);
        }
    }

    private static (IAccessible Node, int ChildId)? HitTest(IAccessible root, PixelPoint point)
    {
        object? result;

        try
        {
            result = root.accHitTest(point.X, point.Y);
        }
        catch
        {
            return null;
        }

        // VT_I4：命中 Root 的某个子元素，node 仍是 root
        if (result is int childId)
            return (root, childId);

        if (result is IAccessible hit)
            return (hit, ChildIdSelf);

        return null;
    }

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

    private static bool IsUsable(PixelRect bounds, RECT window)
    {
        if (bounds.Width < 2 || bounds.Height < 2)
            return false;

        // 必须落在窗口里
        if (bounds.X < window.Left || bounds.Y < window.Top
            || bounds.X + bounds.Width > window.Right
            || bounds.Y + bounds.Height > window.Bottom)
        {
            return false;
        }

        var windowArea = Math.Max(1L, (long)(window.Right - window.Left) * (window.Bottom - window.Top));
        var area = (long)bounds.Width * bounds.Height;

        return area < windowArea * 95 / 100;
    }

    /// <summary>MSAA 里没有框选价值的容器角色。</summary>
    private static bool IsStructuralRole(int role) => role switch
    {
        RoleSystemWindow or RoleSystemClient or RoleSystemDocument
            or RoleSystemPane or RoleSystemGrouping or RoleSystemApplication => true,
        _ => false
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

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    /// <summary>只声明用到的 IAccessible 成员；IDispatch 按 DispId 绑定。</summary>
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessible
    {
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
