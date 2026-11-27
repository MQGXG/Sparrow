using Avalonia;
using Avalonia.Styling;
using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// 原生 Windows Shell 上下文菜单。
///
/// 对搜索结果里的文件/文件夹弹出资源管理器同款菜单：
/// 打开、复制、删除、属性，以及 7-Zip / WPS / 格式工厂等第三方扩展。
/// 走 IContextMenu3 + 一个隐藏消息窗口转发菜单消息，避免自己重画一份。
/// </summary>
internal static class ShellContextMenu
{
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;

    private const int WmInitMenuPopup = 0x0117;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;

    private const uint CmicMaskUnicode = 0x00004000;
    private const uint CmicMaskAsyncOk = 0x00100000;

    private const int HwndMessage = -3;
    private const string WindowClassName = "SparrowShellContextMenu";

    /// <summary>追加到系统菜单末尾的自定义命令 ID，避开 Shell 的 1..0x7FFF 区间。</summary>
    private const int SendToDeviceCommand = 0x8000;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;

    private static IContextMenu3? _activeMenu3;
    private static WndProcDelegate? _wndProc;
    private static bool _classRegistered;

    /// <summary>
    /// 在屏幕坐标 (x, y) 弹出 <paramref name="path"/> 的原生菜单。
    /// 传入 onSendToDevice 时，会在菜单末尾追加「发送到设备…」，
    /// 由 Sparrow 自己处理，不经过 Shell。
    /// 返回 false 表示路径不存在或 Shell 拒绝创建菜单。
    /// </summary>
    public static bool Show(string path, int x, int y, Action? onSendToDevice = null)
    {
        if (string.IsNullOrWhiteSpace(path)
            || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return false;
        }

        // 系统菜单默认跟随 Windows 主题；这里让它跟随 Sparrow 自己的深/浅色
        ApplyMenuTheme();

        var pidl = IntPtr.Zero;
        var hwnd = IntPtr.Zero;
        IContextMenu? contextMenu = null;

        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0
                || pidl == IntPtr.Zero)
            {
                return false;
            }

            var shellFolderIid = typeof(IShellFolder).GUID;

            if (SHBindToParent(pidl, ref shellFolderIid, out var parent, out var childPidl) != 0)
                return false;

            try
            {
                var contextMenuIid = typeof(IContextMenu).GUID;

                if (parent.GetUIObjectOf(
                        IntPtr.Zero,
                        1,
                        [childPidl],
                        ref contextMenuIid,
                        IntPtr.Zero,
                        out var contextMenuPtr) != 0
                    || contextMenuPtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
                    _activeMenu3 = contextMenu as IContextMenu3;

                    hwnd = CreateMessageWindow();
                    var menu = CreatePopupMenu();

                    if (menu == IntPtr.Zero)
                        return false;

                    try
                    {
                        const uint idFirst = 1;
                        const uint idLast = 0x7FFF;

                        contextMenu.QueryContextMenu(
                            menu, 0, idFirst, idLast, CmfNormal | CmfExtendedVerbs);

                        // 在 Shell 菜单末尾追加 Sparrow 自己的动作，命令 ID 避开 Shell 区间
                        if (onSendToDevice is not null)
                        {
                            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                            AppendMenu(menu, MfString, (UIntPtr)SendToDeviceCommand, "发送到设备…");
                        }

                        SetForegroundWindow(hwnd);

                        var command = TrackPopupMenuEx(
                            menu,
                            TpmReturnCmd | TpmLeftAlign | TpmTopAlign,
                            x, y,
                            hwnd,
                            IntPtr.Zero);

                        if (command == SendToDeviceCommand)
                        {
                            Sparrow.App.App.Log.Info("原生菜单：发送到设备");
                            onSendToDevice?.Invoke();
                        }
                        else if (command > 0)
                        {
                            var invoke = new CMINVOKECOMMANDINFOEX
                            {
                                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                                fMask = CmicMaskUnicode | CmicMaskAsyncOk,
                                hwnd = hwnd,
                                lpVerb = (IntPtr)(command - (int)idFirst),
                                nShow = 1
                            };

                            contextMenu.InvokeCommand(ref invoke);
                        }

                        return true;
                    }
                    finally
                    {
                        DestroyMenu(menu);
                    }
                }
                finally
                {
                    if (contextMenu is not null)
                        Marshal.ReleaseComObject(contextMenu);

                    Marshal.Release(contextMenuPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(parent);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            _activeMenu3 = null;

            if (hwnd != IntPtr.Zero)
                DestroyWindow(hwnd);

            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// 让系统菜单按 Sparrow 当前的深/浅色绘制。
    ///
    /// SetPreferredAppMode 是 uxtheme 的未文档化导出（#135），Win10 1903+ 可用；
    /// 老系统上调用会失败，直接忽略即可，菜单回到跟随系统主题。
    /// </summary>
    private static void ApplyMenuTheme()
    {
        try
        {
            // 深色 / 高对比度都按深色菜单处理，只有明确浅色才切浅色
            var dark = !Equals(Application.Current?.ActualThemeVariant, ThemeVariant.Light);

            // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight
            SetPreferredAppMode(dark ? 2 : 3);
            FlushMenuThemes();
        }
        catch
        {
            // 导出不存在或系统不支持：保持系统默认配色
        }
    }

    private static IntPtr CreateMessageWindow()
    {
        EnsureWindowClass();

        return CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            0,
            0, 0, 0, 0,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered)
            return;

        _wndProc = WndProc;

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName
        };

        RegisterClassEx(ref windowClass);
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_activeMenu3 is not null
            && msg is WmInitMenuPopup or WmDrawItem or WmMeasureItem)
        {
            var hr = _activeMenu3.HandleMenuMsg2(msg, wParam, lParam, out var result);

            if (hr == 0)
                return result;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten,
            out IntPtr ppidl,
            ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint cidl,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            ref Guid riid,
            IntPtr rgfReserved,
            out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

        [PreserveSig]
        int SetNameOf(
            IntPtr hwnd,
            IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags,
            out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr commandString, uint cchMax);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F27F1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr commandString, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv,
        out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x, int y, int width, int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
