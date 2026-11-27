using System.Runtime.InteropServices;

namespace ShellMenuProbe;

/// <summary>
/// 原生 Shell 上下文菜单探针：对指定文件/文件夹弹资源管理器同款菜单。
/// 用来验证 IContextMenu3 + 第三方扩展（7-Zip / WPS 等）在当前机器上是否可用。
/// </summary>
internal static class Program
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
    private const string WindowClassName = "SparrowShellMenuProbe";

    private static IContextMenu3? _menu3;
    private static WndProcDelegate? _wndProc;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var path = args.Length > 0
            ? args[0]
            : @"D:\mycodeHub\Sparrow\src\Sparrow.App\Sparrow.App.csproj";

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine($"路径不存在：{path}");
            return 1;
        }

        Console.WriteLine($"目标：{path}");

        var pidl = IntPtr.Zero;
        var hwnd = IntPtr.Zero;

        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                Console.Error.WriteLine("SHParseDisplayName 失败。");
                return 2;
            }

            var shellFolderIid = typeof(IShellFolder).GUID;

            if (SHBindToParent(pidl, ref shellFolderIid, out var parent, out var childPidl) != 0)
            {
                Console.Error.WriteLine("SHBindToParent 失败。");
                return 3;
            }

            try
            {
                var contextMenuIid = typeof(IContextMenu).GUID;
                var hr = parent.GetUIObjectOf(
                    IntPtr.Zero,
                    1,
                    [childPidl],
                    ref contextMenuIid,
                    IntPtr.Zero,
                    out var contextMenuPtr);

                if (hr != 0 || contextMenuPtr == IntPtr.Zero)
                {
                    Console.Error.WriteLine($"GetUIObjectOf 失败：0x{hr:X8}");
                    return 4;
                }

                try
                {
                    var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
                    _menu3 = contextMenu as IContextMenu3;

                    Console.WriteLine(_menu3 is null
                        ? "IContextMenu3 不可用，第三方子菜单可能显示不全。"
                        : "IContextMenu3 已获取。");

                    hwnd = CreateMessageWindow();

                    var menu = CreatePopupMenu();
                    if (menu == IntPtr.Zero)
                    {
                        Console.Error.WriteLine("CreatePopupMenu 失败。");
                        return 5;
                    }

                    try
                    {
                        const uint idFirst = 1;
                        const uint idLast = 0x7FFF;

                        var queryHr = contextMenu.QueryContextMenu(
                            menu, 0, idFirst, idLast, CmfNormal | CmfExtendedVerbs);

                        Console.WriteLine($"QueryContextMenu：0x{queryHr:X8}，菜单项数={GetMenuItemCount(menu)}");

                        // 菜单没有 owner 前台窗口时可能一闪而过
                        SetForegroundWindow(hwnd);

                        var command = TrackPopupMenuEx(
                            menu,
                            TpmReturnCmd | TpmLeftAlign | TpmTopAlign,
                            300, 300,
                            hwnd,
                            IntPtr.Zero);

                        Console.WriteLine($"选择命令：{command}");

                        if (command > 0)
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
                    }
                    finally
                    {
                        DestroyMenu(menu);
                    }
                }
                finally
                {
                    Marshal.Release(contextMenuPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(parent);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 9;
        }
        finally
        {
            _menu3 = null;
            if (hwnd != IntPtr.Zero)
                DestroyWindow(hwnd);
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static IntPtr CreateMessageWindow()
    {
        var instance = GetModuleHandle(null);
        _wndProc = WndProc;

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClassName
        };

        RegisterClassEx(ref windowClass);

        return CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            0,
            0, 0, 0, 0,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_menu3 is not null
            && msg is WmInitMenuPopup or WmDrawItem or WmMeasureItem)
        {
            var hr = _menu3.HandleMenuMsg2(msg, wParam, lParam, out var result);

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

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

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
}
