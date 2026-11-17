using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.App.Overlays;

/// <summary>
/// 拾取窗口 / 图钉跟随所需的 Win32 操作。
///
/// 关键算法移植自 OmniSeeker 的 <c>WindowPinService.GetTopLevelWindowAt</c>：
/// 光标下如果是本进程窗口（命令面板、工作区、拾取遮罩自己），
/// 用 <c>GetWindow(GW_HWNDNEXT)</c> 往下穿透，直到找到别的进程的可见根窗口。
/// 这样面板挡在中间也不会选错。
/// </summary>
internal static class WindowPickerApi
{
    private const uint GaRoot = 2;
    private const uint GwHwndNext = 2;
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>点击时刻解析光标下第一个「别的进程的可见根窗口」；找不到返回 0。</summary>
    public static nint ResolveAtCursor()
    {
        if (!GetCursorPos(out var point))
            return 0;

        var currentProcessId = (uint)Environment.ProcessId;
        var handle = WindowFromPoint(point);

        while (handle != IntPtr.Zero)
        {
            var root = GetAncestor(handle, GaRoot);
            if (root != IntPtr.Zero && IsWindowVisible(root) && !IsShellWindow(root))
            {
                GetWindowThreadProcessId(root, out var processId);
                if (processId != currentProcessId)
                    return root;
            }

            handle = GetWindow(root == IntPtr.Zero ? handle : root, GwHwndNext);
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
