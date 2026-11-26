using System.Runtime.InteropServices;
using Avalonia;

namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图用的滚轮注入。
///
/// 用 SendInput 发真实滚轮事件：合成事件会走系统输入队列，
/// 目标应用（Chromium、Electron、Win32）都按正常滚轮处理，兼容性最好。
/// 注意滚轮事件发给前台窗口，调用方要保证目标应用在前台（截图窗口是
/// ShowActivated=false，关闭后焦点会回到原应用）。
/// </summary>
internal static class WheelScroller
{
    private const uint InputMouse = 0;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x1000;
    private const int WheelDelta = 120;

    /// <summary>滚一次：纵向向下、横向向右。</summary>
    public static void Scroll(bool horizontal, int lines)
    {
        if (!GetCursorPos(out var point))
            return;

        // 滚轮事件走前台窗口。截图窗口刚关、焦点可能还在别的应用（终端等），
        // 不切前台的话滚轮会被前台窗口吃掉，表现为「页面完全没动」。
        var target = GetAncestor(WindowFromPoint(point), GaRoot);
        if (target != IntPtr.Zero && GetForegroundWindow() != target)
        {
            SetForegroundWindow(target);
            Thread.Sleep(20);
        }

        var amount = Math.Max(1, lines) * WheelDelta;
        var delta = horizontal ? amount : -amount;

        var input = new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                MouseData = unchecked((uint)delta),
                Flags = horizontal ? MouseEventHWheel : MouseEventWheel
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public static bool MoveCursor(int x, int y) => SetCursorPos(x, y);

    /// <summary>把光标下的顶层窗口切到前台；自动滚动的滚轮事件走前台窗口。</summary>
    public static bool ActivateTargetAt(PixelPoint point)
    {
        var target = GetAncestor(
            WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }),
            GaRoot);

        return target != IntPtr.Zero && SetForegroundWindow(target);
    }

    public static PixelPoint? GetCursorPosition()
    {
        return GetCursorPos(out var point)
            ? new PixelPoint(point.X, point.Y)
            : null;
    }

    /// <summary>
    /// 一次滚动多少行。模板是帧高的 1/4（40–160px），滚动量一旦超过模板就找不回重叠，
    /// 所以这里固定滚 1 行，宁可多滚几次也不让拼接断掉。
    /// </summary>
    public static int LinesForFrame(int frameHeight) => 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
