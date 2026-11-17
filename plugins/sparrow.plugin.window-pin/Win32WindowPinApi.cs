using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// <see cref="IWindowPinApi"/> 的 Win32 实现。
///
/// 只调用系统 API，不碰宿主内部结构——这是 plugin-model.md「宿主能力边界」
/// 允许的：插件必须可信（ADR-006），系统能力不等于宿主能力。
/// </summary>
public sealed class Win32WindowPinApi : IWindowPinApi
{
    private const int VkLeftButton = 0x01;
    private const int VkEscape = 0x1B;

    private const int GwlExStyle = -20;
    private const long WsExTopMost = 0x00000008;

    private const uint GaRoot = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    // 桌面、任务栏等外壳窗口：置顶没有意义，直接当成「不可选」
    private static readonly string[] ShellWindowClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    public bool IsCancelPressed() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    public bool IsLeftButtonDown() => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    public bool TryGetPointedWindow(out nint handle)
    {
        handle = 0;

        if (!GetCursorPos(out var point))
            return false;

        var underCursor = WindowFromPoint(point);
        if (underCursor == IntPtr.Zero)
            return false;

        // 取根窗口：点在子控件上时，置顶的应该是整个窗口
        var root = GetAncestor(underCursor, GaRoot);
        if (root == IntPtr.Zero || !IsWindow(root))
            return false;

        if (IsShellWindow(root))
            return false;

        handle = root;
        return true;
    }

    public bool TryGetTitle(nint handle, out string title)
    {
        title = string.Empty;

        var buffer = new StringBuilder(512);
        if (GetWindowTextW(handle, buffer, buffer.Capacity) <= 0)
            return false;

        title = buffer.ToString();
        return !string.IsNullOrWhiteSpace(title);
    }

    public bool IsOwnProcessWindow(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public bool IsTopMost(nint handle) =>
        (GetWindowLong(handle, GwlExStyle) & WsExTopMost) != 0;

    public bool SetTopMost(nint handle, bool topMost) =>
        SetWindowPos(
            handle,
            topMost ? HwndTopMost : HwndNoTopMost,
            0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate);

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
