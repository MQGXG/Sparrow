using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Sparrow.App.Shell;

/// <summary>
/// 全局热键策略：手动禁用，以及检测到独占全屏 D3D 游戏时自动暂停。
///
/// 检测只使用 SHQueryUserNotificationState 的 QUNS_RUNNING_D3D_FULL_SCREEN，
/// 再确认前台窗口确实覆盖整块显示器；普通全屏浏览器 / 演示文稿不会触发。
/// 只在自动模式开启且未手动禁用时每 500ms 轮询一次。
/// </summary>
internal sealed class HotkeyPolicyMonitor : IDisposable
{
    private const int QunsRunningD3DFullScreen = 3;
    private const uint MonitorDefaultToNearest = 2;

    private readonly Action<bool> _onSuspendedChanged;
    private readonly IAppLog _log;

    private DispatcherTimer? _timer;
    private bool _manualDisabled;
    private bool _disableInGame;
    private bool _suspended;
    private bool _gameActive;
    private bool _disposed;

    public HotkeyPolicyMonitor(Action<bool> onSuspendedChanged, IAppLog log)
    {
        _onSuspendedChanged = onSuspendedChanged;
        _log = log;
    }

    public bool IsSuspended => _suspended;

    public void Configure(bool manualDisabled, bool disableInGame)
    {
        if (_disposed)
            return;

        _manualDisabled = manualDisabled;
        _disableInGame = disableInGame;

        _timer?.Stop();
        _timer = null;

        if (!_manualDisabled && _disableInGame)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Evaluate();
            _timer.Start();
        }

        Evaluate();
    }

    private void Evaluate()
    {
        if (_disposed)
            return;

        var game = _disableInGame && !_manualDisabled && IsFullScreenGameActive();

        if (game != _gameActive)
        {
            _gameActive = game;
            _log.Info(game
                ? "检测到全屏 D3D 游戏，已暂停全局快捷键"
                : "全屏游戏结束，已恢复全局快捷键");
        }

        var suspended = _manualDisabled || game;
        if (suspended == _suspended)
            return;

        _suspended = suspended;
        _onSuspendedChanged(suspended);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>当前前台窗口是否是独占全屏 D3D 应用（尽力检测，不是通用游戏识别）。</summary>
    internal static bool IsFullScreenGameActive()
    {
        var hr = SHQueryUserNotificationState(out var state);
        if (hr < 0 || state != QunsRunningD3DFullScreen)
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero
            || !IsWindowVisible(foreground)
            || IsIconic(foreground)
            || foreground == GetShellWindow()
            || foreground == GetDesktopWindow())
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId)
            return false;

        if (!GetWindowRect(foreground, out var bounds))
            return false;

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        return bounds.Left <= info.rcMonitor.Left
               && bounds.Top <= info.rcMonitor.Top
               && bounds.Right >= info.rcMonitor.Right
               && bounds.Bottom >= info.rcMonitor.Bottom;
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
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
