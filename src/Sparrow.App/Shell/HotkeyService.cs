using System.Runtime.InteropServices;

namespace Sparrow.App.Shell;

/// <summary>
/// 全局热键（Ctrl + Alt + S）。
///
/// 实现方式：创建一个 message-only 窗口（HWND_MESSAGE），在独立线程上跑自己的消息循环。
/// 为什么不用 Avalonia 主窗口的 HWND + WndProc 钩子：
///   1. 不依赖 Avalonia 内部的 Win32 扩展 API（跨大版本容易变）
///   2. 热键生命周期与主窗口解耦，窗口隐藏/重建不影响注册
///   3. 注册失败可以被明确检测并上报（热键被其它软件占用是最常见的坑）
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5A5A;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    // 按住不放时不重复触发，否则长按 Ctrl+Alt+S 会把面板开关抖成一片
    private const uint ModNoRepeat = 0x4000;
    private const uint VkS = 0x53;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Action _onPressed;
    private readonly WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private IntPtr _hwnd;
    private ushort _atom;
    private volatile bool _registered;
    private volatile bool _disposed;
    private bool _started;

    public HotkeyService(Action onPressed)
    {
        _onPressed = onPressed;
        // 委托必须被字段持有，否则会被 GC 回收，窗口过程变成野指针直接崩溃
        _wndProc = WndProc;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Sparrow.Hotkey"
        };
    }

    /// <summary>
    /// 启动消息循环并注册热键。
    /// 返回 false 表示被其它程序占用或系统拒绝——调用方必须提示用户，
    /// 静默失败会让用户以为软件坏了（这是 M0 的验收项之一）。
    /// </summary>
    public bool Register()
    {
        if (_started)
            return _registered;

        _started = true;
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
        return _registered;
    }

    private void Run()
    {
        var instance = GetModuleHandle(null);
        const string className = "SparrowHotkeyWindow";

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className
        };

        _atom = RegisterClassEx(ref windowClass);
        if (_atom == 0)
        {
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowEx(
            0, className, "Sparrow Hotkey", 0,
            0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        _registered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkS);
        _ready.Set();

        while (!_disposed)
        {
            var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
                break;

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_registered)
            UnregisterHotKey(_hwnd, HotkeyId);

        if (_hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd);

        if (_atom != 0)
            UnregisterClass(className, instance);
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            // 在热键线程触发；调用方负责切回 UI 线程
            _onPressed();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 让 GetMessage 返回 0，从而退出消息循环并清理资源
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);

        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
