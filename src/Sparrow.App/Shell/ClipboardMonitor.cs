using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sparrow.App.Services;

namespace Sparrow.App.Shell;

/// <summary>
/// 系统剪贴板监听。
///
/// 实现方式与 <see cref="HotkeyService"/> 相同：message-only 窗口跑在独立线程上，
/// 用 <c>AddClipboardFormatListener</c> 收 <c>WM_CLIPBOARDUPDATE</c>——事件推送，不轮询。
///
/// 为什么这是宿主能力而不是插件：
/// 插件是「输入 × 参数 → 输出」的纯函数，没有生命周期；
/// 监听必须在宿主运行期间持续工作，并把内容落盘后经 IToolHost.ClipboardHistory 交给插件。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfHDrop = 15;
    private const uint WmClipboardUpdate = 0x031D;

    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>Windows 约定的「不要写入剪贴板历史」格式；密码管理器会设置它。</summary>
    private static readonly uint IgnoreFormatId = RegisterClipboardFormat("Clipboard Viewer Ignore");

    private readonly ClipboardHistoryStore _store;
    private readonly IAppLog _log;
    private readonly WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private IntPtr _hwnd;
    private ushort _atom;
    private volatile bool _paused;
    private volatile bool _disposed;
    private bool _started;

    /// <summary>本程序自己写剪贴板的抑制窗口（UTC ticks）；期间的上报不记历史。</summary>
    private long _suppressUntilTicks;

    /// <summary>
    /// 抑制接下来的一小段时间的剪贴板记录。
    /// 顺序粘贴会主动写剪贴板，不抑制的话粘贴内容会被当成一次用户复制，
    /// 在历史里不断置顶。
    /// </summary>
    public void SuppressNextWrite(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow.Add(duration).UtcTicks;
        Interlocked.Exchange(ref _suppressUntilTicks, until);
    }

    /// <summary>
    /// 暂停记录（隐私开关）。暂停期间剪贴板照常可用，只是不写历史。
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public ClipboardMonitor(ClipboardHistoryStore store, IAppLog log)
    {
        _store = store;
        _log = log;

        // 委托必须被字段持有，否则会被 GC 回收，窗口过程变成野指针直接崩溃
        _wndProc = WndProc;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Sparrow.Clipboard"
        };
    }

    /// <summary>启动监听线程。失败只记日志：历史是增强数据，不能影响应用启动。</summary>
    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        var instance = GetModuleHandle(null);
        const string className = "SparrowClipboardWindow";

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
            _log.Warn($"剪贴板监听窗口注册失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowEx(
            0, className, "Sparrow Clipboard", 0,
            0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _log.Warn($"剪贴板监听窗口创建失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        if (!AddClipboardFormatListener(_hwnd))
            _log.Warn("AddClipboardFormatListener 失败，剪贴板历史不可用");

        _ready.Set();

        while (!_disposed)
        {
            var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
                break;

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            DestroyWindow(_hwnd);
        }

        if (_atom != 0)
            UnregisterClass(className, instance);
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClipboardUpdate && !_paused)
        {
            try
            {
                RecordClipboard();
            }
            catch (Exception ex)
            {
                // 一次记录失败不能让监听线程退出，后面的复制还要继续
                _log.Warn($"记录剪贴板变化失败：{ex.Message}");
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// 读一次剪贴板并落盘。
    /// 优先级：文本 &gt; 文件 &gt; 图片——很多程序复制时会同时塞多种格式，
    /// 取用户「意图上」复制的那一种。
    /// </summary>
    private void RecordClipboard()
    {
        // 自己刚写的剪贴板（顺序粘贴）不记录，避免历史被粘贴内容刷屏
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _suppressUntilTicks))
            return;

        // 复制方可能还握着剪贴板没放，稍等重试几次
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(_hwnd))
                break;

            Thread.Sleep(30);

            if (attempt == 9)
            {
                _log.Warn($"打开剪贴板失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
                return;
            }
        }

        try
        {
            // 密码管理器等会设置这个格式表示「不要记录」；
            // 尊重它，比事后让用户清历史更可靠。
            if (IgnoreFormatId != 0 && IsClipboardFormatAvailable(IgnoreFormatId))
                return;

            var sourceApp = ReadSourceApp();

            var text = ReadText();
            if (!string.IsNullOrEmpty(text))
            {
                _store.AddText(text, sourceApp);
                return;
            }

            var files = ReadFiles();
            if (files is { Length: > 0 })
            {
                _store.AddFiles(files, sourceApp);
                return;
            }

            var png = ReadImagePng();
            if (png is not null)
                _store.AddImage(png, sourceApp);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// 复制来源进程名。拿不到（剪贴板没有 owner、进程已退出）时返回 null，
    /// 只记录进程名，不记录路径或窗口标题。
    /// </summary>
    private static string? ReadSourceApp()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
            return null;

        GetWindowThreadProcessId(owner, out var processId);
        if (processId == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // 进程已退出 / 无权限：来源留空
            return null;
        }
    }

    private string? ReadText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText))
            return null;

        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
            return null;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private string[]? ReadFiles()
    {
        if (!IsClipboardFormatAvailable(CfHDrop))
            return null;

        var handle = GetClipboardData(CfHDrop);
        if (handle == IntPtr.Zero)
            return null;

        var count = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        if (count == 0)
            return null;

        var files = new string[count];
        for (var index = 0u; index < count; index++)
        {
            var length = DragQueryFile(handle, index, null, 0);
            var buffer = new StringBuilder((int)length + 1);
            DragQueryFile(handle, index, buffer, (uint)buffer.Capacity);
            files[index] = buffer.ToString();
        }

        return files;
    }

    private byte[]? ReadImagePng()
    {
        if (!IsClipboardFormatAvailable(CfDib))
            return null;

        var handle = GetClipboardData(CfDib);
        if (handle == IntPtr.Zero)
            return null;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
                return null;

            var dib = new byte[size];
            Marshal.Copy(pointer, dib, 0, size);

            // DIB → PNG 的转换逻辑与宿主写图共用一份，避免两处漂移
            return Win32Clipboard.DibToPng(dib);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

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
    private static extern ushort UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr dropHandle, uint index, StringBuilder? fileName, uint bufferLength);
}
