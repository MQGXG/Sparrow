using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Sparrow.App.Services;

/// <summary>
/// Windows 原生窗口外观的小补丁。
/// Avalonia 的 RequestedThemeVariant 只管控件主题，标题栏仍跟随系统亮色，
/// 这里用 DWM 把标题栏切成深色，避免深色窗口顶着一整条白条。
/// </summary>
internal static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);

    public static void TryApplyDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == IntPtr.Zero)
            return;

        var enabled = 1;

        // Windows 10 1809+ 用 20；更早的预览版本用 19
        if (DwmSetWindowAttribute(handle.Value, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle.Value, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }
}
