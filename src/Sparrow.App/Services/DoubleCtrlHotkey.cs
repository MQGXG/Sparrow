using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// 双击 Ctrl 的全局检测。
///
/// Windows 没有现成的「双击修饰键」热键，只能用 WH_KEYBOARD_LL 低级钩子。
/// 钩子回调里只做时间判断，触发时把工作交给调用方 Post 到 UI 线程——
/// 绝不能在这里做重活，否则整个系统的键盘输入都会被拖慢。
///
/// 判定规则：一次「干净的 Ctrl」按下并抬起（期间没按其他键）算一次，
/// 两次之间的间隔小于阈值才触发。这样 Ctrl+C / Ctrl+Shift 不会误触。
/// </summary>
internal sealed class DoubleCtrlHotkey : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;

    private const uint VkControl = 0x11;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;

    private readonly Action _onDoubleCtrl;
    private readonly IAppLog _log;
    private readonly int _maxGapMs;
    private readonly KeyboardProc _proc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _allowed = true;
    private volatile bool _disposed;
    private bool _failed;

    // 只在钩子线程读写，不需要锁
    private bool _ctrlDown;
    private bool _ctrlClean;
    private long _lastCleanUpAt;

    public DoubleCtrlHotkey(Action onDoubleCtrl, IAppLog log, int maxGapMs = 400)
    {
        _onDoubleCtrl = onDoubleCtrl;
        _log = log;
        _maxGapMs = Math.Clamp(maxGapMs, 150, 800);

        // 委托必须被字段持有，否则会被 GC 回收，回调变成野指针直接崩溃
        _proc = HookProc;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Sparrow.DoubleCtrl"
        };
    }

    /// <summary>安装钩子；失败只记日志，不影响程序启动。</summary>
    public bool Start()
    {
        if (_thread.IsAlive)
            return _hook != IntPtr.Zero;

        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
        return !_failed && _hook != IntPtr.Zero;
    }

    /// <summary>暂停 / 恢复双击 Ctrl；暂停时钩子保留，但不触发。</summary>
    public void SetAllowed(bool allowed) => _allowed = allowed;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        _ready.Set();

        if (_hook == IntPtr.Zero)
        {
            _failed = true;
            _log.Warn($"双击 Ctrl 钩子安装失败：{Marshal.GetLastWin32Error()}");
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
            if (!_allowed)
            {
                _ctrlDown = false;
                _ctrlClean = false;
                _lastCleanUpAt = 0;
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            var message = (uint)wParam.ToInt64();
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var isCtrl = info.VkCode is VkControl or VkLControl or VkRControl;

            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (isCtrl)
                {
                    // 按住不放时系统会重复发 keydown，只认第一次
                    if (!_ctrlDown)
                    {
                        _ctrlDown = true;
                        _ctrlClean = true;
                    }
                }
                else
                {
                    // Ctrl 按住期间又按了别的键：这是一次组合键，不算双击
                    _ctrlClean = false;
                }
            }
            else if ((message is WmKeyUp or WmSysKeyUp) && isCtrl)
            {
                _ctrlDown = false;

                if (_ctrlClean)
                {
                    var now = Environment.TickCount64;

                    if (_lastCleanUpAt != 0 && now - _lastCleanUpAt <= _maxGapMs)
                    {
                        _lastCleanUpAt = 0;
                        Trigger();
                    }
                    else
                    {
                        _lastCleanUpAt = now;
                    }
                }

                _ctrlClean = false;
            }
        }

        // 永远放行，钩子只观察，不吞键
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Trigger()
    {
        try
        {
            _onDoubleCtrl();
        }
        catch (Exception ex)
        {
            // 低级钩子回调里抛异常会直接带走进程，这里必须兜住
            _log.Error("双击 Ctrl 回调失败", ex);
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, KeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

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
