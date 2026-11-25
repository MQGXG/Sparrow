// UiElementProbe 分部类：Win32 P/Invoke 声明。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

public sealed partial class UiElementProbe
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    private static string? GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// 对渲染子窗口做一次 IAccessible 查询。Chrome/Edge 收到这个请求后才会
    /// 真正构建网页无障碍树；否则 UIA 的 ControlView 只有分组/文档。
    /// </summary>
    private static void PingAccessibility(IntPtr hwnd)
    {
        object? accessible = null;

        try
        {
            var iid = IidIAccessible;
            var hr = AccessibleObjectFromWindow(hwnd, ObjIdClient, ref iid, out accessible);

            if (hr != 0 || accessible is null)
                return;

            SelectorTrace.Write($"accessibility ping 0x{hwnd.ToInt64():X}");
        }
        catch
        {
            // 目标不支持 IAccessible：忽略
        }
        finally
        {
            if (accessible is not null && Marshal.IsComObject(accessible))
            {
                try
                {
                    Marshal.FinalReleaseComObject(accessible);
                }
                catch
                {
                }
            }
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint objectId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? accessible);
}
