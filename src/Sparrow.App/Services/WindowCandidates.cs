using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Sparrow.App.Services;

/// <summary>光标下的一个顶层窗口候选。</summary>
public sealed record WindowCandidate(IntPtr Handle, string Title, PixelRect Bounds, int ZOrder)
{
    public PixelPoint Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);

    public bool Contains(PixelPoint point) =>
        point.X >= Bounds.X && point.X < Bounds.X + Bounds.Width &&
        point.Y >= Bounds.Y && point.Y < Bounds.Y + Bounds.Height;
}

/// <summary>
/// 枚举屏幕上的顶层窗口，供截图时「鼠标悬停选区」使用。
///
/// 不做 UIA 控件级识别：那是另一层能力，对 Electron/自绘界面也不稳定。
/// 这里只给窗口矩形，稳定、快，且不碰我们的遮罩（按进程过滤）。
/// </summary>
public static class WindowCandidates
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const uint DwmwaCloaked = 14;
    private const uint DwmwaExtendedFrameBounds = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>
    /// 按 Z 序（最上层在前）返回可见窗口；排除本进程窗口。
    ///
    /// 和 snow-shot 的规则一致：只排除「点击穿透」的 layered 窗口
    /// （WS_EX_LAYERED + WS_EX_TRANSPARENT），不按工具窗口 / shell 类 / 大小过滤。
    /// 否则任务栏、托盘、弹出菜单、浮动工具条这些地方会去探测后面的应用，
    /// 或者干脆没有候选。
    /// </summary>
    public static IReadOnlyList<WindowCandidate> Enumerate(IntPtr excludeHandle)
    {
        var candidates = new List<WindowCandidate>();
        var processId = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHandle || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId == processId)
                return true;

            if (IsCloaked(hwnd))
                return true;

            // 点击穿透的 layered 窗口（手写画布、透明 HUD）不参与命中，
            // 否则它会挡住下面所有真实窗口。
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            if ((exStyle & WsExLayered) != 0 && (exStyle & WsExTransparent) != 0)
                return true;

            if (!TryGetVisibleRect(hwnd, out var rect))
                return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return true;

            var title = GetWindowTitle(hwnd);
            var className = GetClassName(hwnd);
            candidates.Add(new WindowCandidate(
                hwnd,
                string.IsNullOrWhiteSpace(title) ? className ?? "窗口" : title,
                new PixelRect(rect.Left, rect.Top, width, height),
                candidates.Count));

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

        var index = Array.FindIndex(under, candidate => candidate.Handle == current.Handle);
        return index >= 0 && index + 1 < under.Length ? under[index + 1] : null;
    }

    /// <summary>当前鼠标位置（物理像素）。</summary>
    public static PixelPoint GetCursorPosition() =>
        GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    /// <summary>
    /// 优先用 DWM 扩展边框：GetWindowRect 在 Win10/11 上包含不可见的缩放边框，
    /// 框选时会把一圈透明边也算进去。DWM 失败再退回 GetWindowRect。
    /// </summary>
    private static bool TryGetVisibleRect(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowRect(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0
            && rect.Right - rect.Left >= 16
            && rect.Bottom - rect.Top >= 16)
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowRect(
        IntPtr hwnd, uint attribute, out RECT rect, int size);
}
