using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Sparrow.App.Services;

/// <summary>
/// 把窗口从截图 / 录屏里排除（WDA_EXCLUDEFROMCAPTURE，Win10 2004+）。
///
/// 用于「滚动截图时捕获界面」关闭时：滚动提示条本身也是窗口，
/// 不排除就会被拼进最终长截图。
/// </summary>
public static class WindowCaptureExclusion
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool Exclude(Window window) => Set(window, WdaExcludeFromCapture);

    public static bool Restore(Window window) => Set(window, WdaNone);

    private static bool Set(Window window, uint affinity)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            return SetWindowDisplayAffinity(hwnd, affinity);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
