using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Sparrow.App.Services;

/// <summary>光标下的一个顶层窗口候选。</summary>
public sealed record WindowCandidate(
    IntPtr Handle,
    string Title,
    PixelRect Bounds,
    int ZOrder,
    string? ClassName = null)
{
    public PixelPoint Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);

    public bool Contains(PixelPoint point) =>
        point.X >= Bounds.X && point.X < Bounds.X + Bounds.Width &&
        point.Y >= Bounds.Y && point.Y < Bounds.Y + Bounds.Height;
}

/// <summary>
/// 枚举屏幕上的顶层窗口，供截图时「鼠标悬停选区」使用。
///
/// 与 Snow Shot 的窗口快照对齐：
/// - 只排除显式传入的 HWND（截图遮罩及其子窗口），同进程的钉图窗口仍可被选中；
/// - 可见矩形按显示器裁剪；桌面宿主（Progman/WorkerW/Shell）按屏拆成多个候选，
///   普通跨屏窗口保留完整并集；
/// - 命中按 Z 序（最上层在前），不做 UIA 控件级识别。
/// </summary>
public static class WindowCandidates
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const uint DwmwaCloaked = 14;
    private const uint DwmwaExtendedFrameBounds = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate bool MonitorEnumProc(
        IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    /// <summary>兼容旧调用：只排除一个窗口。</summary>
    public static IReadOnlyList<WindowCandidate> Enumerate(IntPtr excludeHandle) =>
        Enumerate(excludeHandle == IntPtr.Zero ? [] : [excludeHandle]);

    /// <summary>
    /// 按 Z 序（最上层在前）返回可见窗口。
    /// <paramref name="excluded"/> 里的 HWND 及其子窗口不参与命中。
    /// </summary>
    public static IReadOnlyList<WindowCandidate> Enumerate(
        IReadOnlyList<IntPtr> excluded,
        IReadOnlyList<PixelRect>? displays = null)
    {
        var candidates = new List<WindowCandidate>();
        var excludedSet = ExpandExcluded(excluded);
        IReadOnlyList<PixelRect> monitors = displays is { Count: > 0 }
            ? displays
            : GetMonitors();

        EnumWindows((hwnd, _) =>
        {
            if (excludedSet.Contains(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            if (IsCloaked(hwnd))
                return true;

            var title = GetWindowTitle(hwnd);
            var className = GetClassName(hwnd);

            // 兜底：遮罩 HWND 没拿到时，按进程 + 标题排除截图窗口本身，
            // 否则整屏遮罩会一直命中自己，浏览器永远轮不到。
            GetWindowThreadProcessId(hwnd, out var windowProcessId);

            if (windowProcessId == Environment.ProcessId
                && (string.Equals(title, "SparrowCapture", StringComparison.Ordinal)
                    || string.Equals(className, "CaptureWindow", StringComparison.Ordinal)))
            {
                return true;
            }

            // 点击穿透的 layered 窗口（手写画布、透明 HUD）不参与命中，
            // 否则它会挡住下面所有真实窗口。
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            if ((exStyle & WsExLayered) != 0 && (exStyle & WsExTransparent) != 0)
                return true;

            if (!TryGetVisibleRect(hwnd, out var rect))
                return true;

            var displayTitle = string.IsNullOrWhiteSpace(title)
                ? className ?? "窗口"
                : title;

            var windowBounds = new PixelRect(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);

            // 跨屏普通窗口取并集；桌面宿主按显示器拆开，保证命中当前屏
            foreach (var bounds in SelectionRects(
                         windowBounds, IsDesktopWindow(hwnd, className), monitors))
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;

                candidates.Add(new WindowCandidate(
                    hwnd,
                    displayTitle,
                    bounds,
                    candidates.Count,
                    className));
            }

            return true;
        }, IntPtr.Zero);

        return candidates;
    }

    /// <summary>返回包含该点的最上层窗口。</summary>
    public static WindowCandidate? Pick(
        IReadOnlyList<WindowCandidate> candidates,
        PixelPoint point) =>
        candidates.FirstOrDefault(candidate => candidate.Contains(point));

    /// <summary>按 Z 序循环：当前候选的下一个；到末尾返回 null（隐藏候选）。</summary>
    public static WindowCandidate? Next(
        IReadOnlyList<WindowCandidate> candidates,
        PixelPoint point,
        WindowCandidate? current)
    {
        var under = candidates.Where(candidate => candidate.Contains(point)).ToArray();
        if (under.Length == 0)
            return null;

        if (current is null)
            return under[0];

        var index = Array.FindIndex(under, candidate => candidate.Equals(current));
        return index >= 0 && index + 1 < under.Length ? under[index + 1] : null;
    }

    /// <summary>当前鼠标位置（物理像素）。</summary>
    public static PixelPoint GetCursorPosition() =>
        GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    /// <summary>当前前台窗口句柄；截图为「预建 UIA 树」用它确定目标窗口。</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>
    /// 浏览器 / Electron / UWP 窗口。这类窗口被截图遮罩完全盖住后，
    /// Chromium 会关闭网页无障碍树，之后再查 UIA 只剩窗口/文档/窗格；
    /// 因此必须在遮罩显示前先建树（见 App.PrewarmCaptureProbeAsync）。
    /// </summary>
    public static bool IsBrowserLikeWindow(IntPtr hwnd) => GetClassName(hwnd) is
        "Chrome_WidgetWin_1" or "Chrome_WidgetWin_0" or "MozillaWindowClass"
        or "ApplicationFrameWindow" or "CefWebView" or "CefBrowserWindow" or "WebViewHost";

    /// <summary>当前所有显示器的物理像素矩形。</summary>
    public static IReadOnlyList<PixelRect> GetMonitors()
    {
        var monitors = new List<PixelRect>(4);

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                var rect = info.rcMonitor;
                monitors.Add(new PixelRect(
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top));
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    private static HashSet<IntPtr> ExpandExcluded(IReadOnlyList<IntPtr> excluded)
    {
        var set = new HashSet<IntPtr>();

        foreach (var handle in excluded)
        {
            if (handle == IntPtr.Zero || !set.Add(handle))
                continue;

            EnumChildWindows(handle, (child, _) =>
            {
                set.Add(child);
                return true;
            }, IntPtr.Zero);
        }

        return set;
    }

    /// <summary>
    /// 把窗口可见矩形映射为候选矩形：
    /// - 桌面宿主按显示器拆分（每个屏幕一个候选）；
    /// - 普通窗口跨屏时取所有交集的并集。
    /// </summary>
    internal static IReadOnlyList<PixelRect> SelectionRects(
        PixelRect window,
        bool desktop,
        IReadOnlyList<PixelRect> monitors)
    {
        var intersections = new List<PixelRect>(monitors.Count);

        foreach (var monitor in monitors)
        {
            var intersection = Intersect(window, monitor);
            if (!IsEmpty(intersection))
                intersections.Add(intersection);
        }

        if (intersections.Count == 0)
            return [];

        if (intersections.Count == 1 || !desktop)
        {
            var union = intersections[0];

            for (var i = 1; i < intersections.Count; i++)
                union = Union(union, intersections[i]);

            return [union];
        }

        return intersections;
    }

    private static bool IsDesktopWindow(IntPtr hwnd, string? className)
    {
        if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            return true;

        return className is "Progman" or "WorkerW";
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

    private static PixelRect Union(PixelRect left, PixelRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var rightEdge = Math.Max(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Max(left.Y + left.Height, right.Y + right.Height);

        return new PixelRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private static bool IsEmpty(PixelRect rect) => rect.Width <= 0 || rect.Height <= 0;

    /// <summary>
    /// 优先用 DWM 扩展边框：GetWindowRect 在 Win10/11 上包含不可见的缩放边框，
    /// 框选时会把一圈透明边也算进去。DWM 失败再退回 GetWindowRect。
    /// </summary>
    private static bool TryGetVisibleRect(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowRect(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0
            && rect.Right - rect.Left >= 2
            && rect.Bottom - rect.Top >= 2)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            var result = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
            return result == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowRect(
        IntPtr hwnd, uint attribute, out RECT rect, int size);
}
