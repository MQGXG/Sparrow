using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.App.Overlays;

/// <summary>
/// 拾取窗口 / 图钉跟随所需的 Win32 操作。
///
/// 命中规则：按 Z 序枚举所有顶层窗口，只取「覆盖光标点、非本进程、可见、
/// 未最小化、非 Shell」的第一个。旧实现用 GetWindow(GW_HWNDNEXT) 沿 Z 序
/// 盲穿透，选到的窗口可能根本不在光标下（从托盘菜单启动时尤其容易误选），
/// 于是「点了一个窗口，却把另一个窗口置顶、只留一个图钉在最前面」。
/// </summary>
internal static class WindowPickerApi
{
    private const uint GaRoot = 2;
    private const int GwlExStyle = -20;
    private const long WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    private static readonly string[] ShellWindowClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    ];

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>一次窗口枚举的快照；候选按 Z 序（最前 → 最后）传入命中判断。</summary>
    internal readonly record struct WindowCandidate(
        nint Handle,
        uint ProcessId,
        bool Visible,
        bool Minimized,
        bool IsShell,
        Rect Bounds);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>读取当前光标位置（物理像素）。</summary>
    public static bool TryGetCursor(out Point point) => GetCursorPos(out point);

    /// <summary>点击时刻解析光标下最靠前的、非本进程的可见顶层窗口；找不到返回 0。</summary>
    public static nint ResolveAtCursor()
    {
        if (!GetCursorPos(out var point))
            return 0;

        var currentProcessId = (uint)Environment.ProcessId;
        var candidates = new List<WindowCandidate>();

        // EnumWindows 的回调顺序就是 Z 序；遮罩 / 面板等本进程窗口会在命中判断里被排除，
        // 所以不再依赖「Hide() 之后遮罩是否真的已从命中链移除」。
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            GetWindowRect(handle, out var bounds);

            candidates.Add(new WindowCandidate(
                handle,
                processId,
                IsWindowVisible(handle),
                IsIconic(handle),
                IsShellWindow(handle),
                bounds));

            return true;
        }, IntPtr.Zero);

        var picked = SelectWindowAt(point, currentProcessId, candidates);
        if (picked != 0)
            return picked;

        // 兜底：异形窗口 / DPI 虚拟化可能让矩形不准，直接用光标下的根窗口，
        // 但绝不沿 Z 序盲穿透——找不到就当取消，避免置顶到不相干的窗口。
        var underCursor = WindowFromPoint(point);
        if (underCursor == IntPtr.Zero)
            return 0;

        var root = GetAncestor(underCursor, GaRoot);
        if (root == IntPtr.Zero || !IsWindowVisible(root) || IsShellWindow(root))
            return 0;

        GetWindowThreadProcessId(root, out var rootProcessId);
        return rootProcessId != 0 && rootProcessId != currentProcessId ? root : 0;
    }

    /// <summary>纯命中判断：返回第一个覆盖光标点的合格窗口；没有就返回 0（取消）。</summary>
    internal static nint SelectWindowAt(
        Point point,
        uint currentProcessId,
        IReadOnlyList<WindowCandidate> candidatesInZOrder)
    {
        foreach (var candidate in candidatesInZOrder)
        {
            if (!candidate.Visible || candidate.Minimized || candidate.IsShell)
                continue;
            if (candidate.ProcessId == 0 || candidate.ProcessId == currentProcessId)
                continue;

            var bounds = candidate.Bounds;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                continue;
            if (point.X < bounds.Left || point.X >= bounds.Right)
                continue;
            if (point.Y < bounds.Top || point.Y >= bounds.Bottom)
                continue;

            return candidate.Handle;
        }

        return 0;
    }

    public static string GetTitle(nint handle)
    {
        var buffer = new StringBuilder(512);

        // GetWindowTextW 使用 GetWindowText 的 Unicode 入口
        var length = GetWindowTextW(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    public static bool Exists(nint handle) => IsWindow(handle);

    /// <summary>窗口所属进程；窗口无效时返回 0。</summary>
    public static uint GetProcessId(nint handle) =>
        GetWindowThreadProcessId(handle, out var processId) != 0 ? processId : 0;

    public static bool IsMinimized(nint handle) => IsIconic(handle);

    public static Rect GetBounds(nint handle)
    {
        GetWindowRect(handle, out var rect);
        return rect;
    }

    public static bool IsTopMost(nint handle) =>
        (GetWindowLong(handle, GwlExStyle) & WsExTopMost) != 0;

    public static void SetTopMost(nint handle, bool topMost) =>
        SetWindowPos(
            handle,
            topMost ? HwndTopMost : HwndNoTopMost,
            0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    private static long GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex).ToInt64()
            : GetWindowLong32(hWnd, nIndex);

    private static bool IsShellWindow(IntPtr handle)
    {
        var buffer = new StringBuilder(128);
        if (GetClassNameW(handle, buffer, buffer.Capacity) <= 0)
            return false;

        var className = buffer.ToString();
        foreach (var shell in ShellWindowClasses)
        {
            if (string.Equals(className, shell, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
