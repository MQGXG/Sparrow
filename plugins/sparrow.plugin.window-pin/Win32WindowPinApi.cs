using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// <see cref="IWindowPinApi"/> 的 Win32 实现。
///
/// 只调用系统 API，不创建窗口、不碰宿主内部结构——
/// 窗口拾取与图钉由宿主的 <c>IWindowOverlay</c> 负责（见 plugin-model.md 宿主能力边界）。
/// </summary>
public sealed class Win32WindowPinApi : IWindowPinApi
{
    private const int GwlExStyle = -20;
    private const long WsExTopMost = 0x00000008;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    public bool TryGetTitle(nint handle, out string title)
    {
        title = string.Empty;

        var buffer = new StringBuilder(512);
        if (GetWindowTextW(handle, buffer, buffer.Capacity) <= 0)
            return false;

        title = buffer.ToString();
        return !string.IsNullOrWhiteSpace(title);
    }

    public bool IsTopMost(nint handle) =>
        (GetWindowLong(handle, GwlExStyle) & WsExTopMost) != 0;

    public bool SetTopMost(nint handle, bool topMost) =>
        SetWindowPos(
            handle,
            topMost ? HwndTopMost : HwndNoTopMost,
            0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate);
}
