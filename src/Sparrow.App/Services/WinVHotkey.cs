using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// 可选的 Win+V 接管。
///
/// Windows 把 Win+V 保留给自带剪贴板，普通 RegisterHotKey 抢不过来；
/// 这里用 WH_KEYBOARD_LL 在系统处理之前吞掉这个组合，再通知宿主打开自己的剪贴板面板。
///
/// 和双击 Ctrl 一样，钩子跑在独立线程上：低级键盘钩子回调一旦被 UI 线程拖慢，
/// Windows 会静默移除钩子（LowLevelHooksTimeout）。
/// </summary>
internal sealed class WinVHotkey : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;

    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkMenu = 0x12;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkV = 0x56;

    // 发送一个“已被处理”的中性键，避免松开 Win 时弹出开始菜单。
    private const ushort VkNeutral = 0xE8;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 2;

    private readonly Action _onTrigger;
    private readonly IAppLog _log;
    private readonly KeyboardProc _proc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _disposed;
    private bool _failed;

    // 只在钩子线程读写
    private bool _held;
    private bool _winDown;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;

    public WinVHotkey(Action onTrigger, IAppLog log)
    {
        _onTrigger = onTrigger;
        _log = log;

        // 委托必须被字段持有，否则会被 GC 回收，回调变成野指针
        _proc = HookProc;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Sparrow.WinV"
        };
    }

    public bool Start()
    {
        if (_thread.IsAlive)
            return _hook != IntPtr.Zero;

        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
        return !_failed && _hook != IntPtr.Zero;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        _ready.Set();

        if (_hook == IntPtr.Zero)
        {
            _failed = true;
            _log.Warn($"Win+V 钩子安装失败：{Marshal.GetLastWin32Error()}");
            return;
        }

        while (!_disposed)
        {
            var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
                break;

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            var message = (uint)wParam.ToInt64();
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var down = message is WmKeyDown or WmSysKeyDown;
            UpdateModifier(info.VkCode, down);

            if (info.VkCode == VkV)
            {
                if (_held)
                {
                    // 按住 V 的重复键和最后的 keyup 也要吞掉，否则会漏给系统
                    if (!down)
                        _held = false;

                    return (IntPtr)1;
                }

                if (down && _winDown && !_ctrlDown && !_altDown && !_shiftDown)
                {
                    _held = true;
                    MarkChordUsed();
                    Trigger();
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>在钩子线程跟踪修饰键状态，避免依赖 GetAsyncKeyState 的时序。</summary>
    private void UpdateModifier(uint vk, bool down)
    {
        if (vk is VkLWin or VkRWin)
            _winDown = down;
        else if (vk is VkControl or VkLControl or VkRControl)
            _ctrlDown = down;
        else if (vk is VkMenu or VkLMenu or VkRMenu)
            _altDown = down;
        else if (vk is VkShift or VkLShift or VkRShift)
            _shiftDown = down;
    }

    private void Trigger()
    {
        try
        {
            _log.Info("Win+V 触发剪贴板面板");
            _onTrigger();
        }
        catch (Exception ex)
        {
            // 低级钩子回调里抛异常会直接带走进程
            _log.Error("Win+V 回调失败", ex);
        }
    }

    /// <summary>告诉系统这次 Win 组合已经被使用，避免开始菜单被误触发。</summary>
    private static void MarkChordUsed()
    {
        var inputs = new[]
        {
            new INPUT
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT { WVk = VkNeutral }
                }
            },
            new INPUT
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT { WVk = VkNeutral, DwFlags = KeyEventKeyUp }
                }
            }
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    private delegate IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, KeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint idThread, uint message, IntPtr wParam, IntPtr lParam);
}
