using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Sparrow.App.Services;

/// <summary>
/// Windows 原生窗口外观的小补丁。
/// Avalonia 的 RequestedThemeVariant 只管控件主题，标题栏仍跟随系统亮色，
/// 这里用 DWM 把系统标题栏刷成与应用主题一致，避免「白窗口 + 黑标题栏」。
/// </summary>
internal static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);

    /// <summary>兼容入口：暗房类窗口恒定为深色。</summary>
    public static void TryApplyDarkTitleBar(Window window) => TryApplyTitleBarTheme(window, dark: true);

    /// <summary>
    /// 按应用当前主题设置系统标题栏。窗口的 ActualThemeVariant 变化时
    /// （含「跟随系统」模式下系统切换主题）由各窗口自行重新调用。
    /// </summary>
    public static void TryApplyTitleBarTheme(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == IntPtr.Zero)
            return;

        var enabled = dark ? 1 : 0;

        // Windows 10 1809+ 用 20；更早的预览版本用 19
        if (DwmSetWindowAttribute(handle.Value, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle.Value, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

        // 扩展标题栏时平台可能仍会给右上角按钮区刷一层系统标题栏底色；
        // 刷成窗口底色，顶栏才是一整块。COLORREF = 0x00BBGGRR。
        // 深色 = #0D1118，浅色 = #F7F8FA。
        var captionColor = dark ? 0x0018110D : 0x00FAF8F7;
        DwmSetWindowAttribute(handle.Value, DwmwaCaptionColor, ref captionColor, sizeof(int));
    }
}
