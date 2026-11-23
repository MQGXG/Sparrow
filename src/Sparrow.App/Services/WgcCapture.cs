using System.Runtime.InteropServices;
using Avalonia;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Sparrow.App.Services;

/// <summary>
/// Windows Graphics Capture（WGC）屏幕区域捕获。
///
/// 只在「目标矩形完整落在一个显示器里」时使用；跨屏 / 不支持的系统返回 null，
/// 由 <see cref="ScreenCapture"/> 回退 GDI。WGC 走桌面合成器，能抓到 GDI
/// 抓不到的硬件加速内容和部分受保护窗口。
///
/// 设备只创建一次；每帧创建 frame pool 并轮询 <c>TryGetNextFrame</c>，
/// 不依赖 DispatcherQueue（后台线程没有消息泵，事件模型不会触发）。
/// </summary>
internal static class WgcCapture
{
    private static readonly Guid IidGraphicsCaptureItem =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static readonly Guid IidGraphicsCaptureItemInterop =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    private static readonly Guid IidDxgiDevice =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private const int D3dDriverTypeHardware = 1;
    private const int D3d11SdkVersion = 7;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>抓一帧最多等多久（毫秒）。</summary>
    private const int FrameTimeoutMilliseconds = 1200;

    private static readonly Lazy<IDirect3DDevice?> SharedDevice = new(
        CreateD3DDevice,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>WGC 是否可用于这个系统。</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                       && GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 捕获屏幕物理像素区域（BGRA，top-down）。失败 / 跨屏返回 null。
    /// </summary>
    public static byte[]? CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || !IsSupported)
            return null;

        if (!TryGetSingleMonitor(
                new PixelRect(x, y, width, height),
                out var monitor,
                out var monitorRect))
        {
            return null;
        }

        if (SharedDevice.Value is not { } device)
            return null;

        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            item = CreateItemForMonitor(monitor);
            if (item is null)
                return null;

            pool = Direct3D11CaptureFramePool.Create(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();

            var deadline = Environment.TickCount64 + FrameTimeoutMilliseconds;

            while (Environment.TickCount64 < deadline)
            {
                using var frame = pool.TryGetNextFrame();
                if (frame is not null)
                {
                    return Extract(
                        frame,
                        new PixelRect(
                            x - monitorRect.X,
                            y - monitorRect.Y,
                            width,
                            height));
                }

                Thread.Sleep(8);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
        }
    }

    private static byte[]? Extract(Direct3D11CaptureFrame frame, PixelRect crop)
    {
        using var bitmap = SoftwareBitmap
            .CreateCopyFromSurfaceAsync(frame.Surface)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (bitmap is null)
            return null;

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var description = buffer.GetPlaneDescription(0);
        var planeWidth = description.Width;
        var planeHeight = description.Height;

        if (crop.X < 0 || crop.Y < 0
            || crop.X + crop.Width > planeWidth
            || crop.Y + crop.Height > planeHeight)
        {
            return null;
        }

        var reference = buffer.CreateReference();

        try
        {
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var source, out _);

            var stride = description.Stride;
            var rowBytes = crop.Width * 4;
            var pixels = new byte[rowBytes * crop.Height];

            for (var row = 0; row < crop.Height; row++)
            {
                var sourceOffset = description.StartIndex
                                   + (crop.Y + row) * stride
                                   + crop.X * 4;

                Marshal.Copy(
                    source + sourceOffset,
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }

            return pixels;
        }
        finally
        {
            if (reference is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr monitor)
    {
        var interop = GetItemInterop();
        if (interop is null)
            return null;

        var iid = IidGraphicsCaptureItem;

        if (interop.CreateForMonitor(monitor, ref iid, out var item) < 0 || item == IntPtr.Zero)
            return null;

        // FromAbi 接管这个原生引用，不要在这里 Release
        return GraphicsCaptureItem.FromAbi(item);
    }

    private static IGraphicsCaptureItemInterop? GetItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        if (WindowsCreateString(className, className.Length, out var name) < 0)
            return null;

        try
        {
            var iid = IidGraphicsCaptureItemInterop;

            if (RoGetActivationFactory(name, ref iid, out var factory) < 0
                || factory == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static IDirect3DDevice? CreateD3DDevice()
    {
        try
        {
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3dDriverTypeHardware,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out var device,
                out _,
                out var context);

            if (hr < 0 || device == IntPtr.Zero)
                return null;

            try
            {
                var iid = IidDxgiDevice;

                if (Marshal.QueryInterface(device, in iid, out var dxgiDevice) < 0)
                    return null;

                try
                {
                    if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable) < 0
                        || inspectable == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        // 用 CsWinRT 的 ABI 包装：GetObjectForIUnknown 得到的是 RCW，
                        // 再传给 WinRT 会尝试建 CCW 并失败。
                        return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }
            }
            finally
            {
                if (context != IntPtr.Zero)
                    Marshal.Release(context);

                Marshal.Release(device);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目标矩形必须完整落在一个显示器里；跨屏交给 GDI。</summary>
    private static bool TryGetSingleMonitor(
        PixelRect rect,
        out IntPtr monitor,
        out PixelRect monitorRect)
    {
        monitor = IntPtr.Zero;
        monitorRect = default;

        var native = new RECT
        {
            Left = rect.X,
            Top = rect.Y,
            Right = rect.X + rect.Width,
            Bottom = rect.Y + rect.Height
        };

        monitor = MonitorFromRect(ref native, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (!GetMonitorInfo(monitor, ref info))
            return false;

        monitorRect = new PixelRect(
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top);

        return rect.X >= monitorRect.X
               && rect.Y >= monitorRect.Y
               && rect.X + rect.Width <= monitorRect.X + monitorRect.Width
               && rect.Y + rect.Height <= monitorRect.Y + monitorRect.Height;
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

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
