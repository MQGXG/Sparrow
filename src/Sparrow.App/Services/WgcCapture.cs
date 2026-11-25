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
/// 跨多个显示器时逐屏抓取再拼接（每屏一个 capture item）；不支持的系统返回 null，
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

    /// <summary>抓一帧最多等多久（毫秒）。</summary>
    private const int FrameTimeoutMilliseconds = 1200;

    private static readonly Lazy<IDirect3DDevice?> SharedDevice = new(
        CreateD3DDevice,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>共享 D3D 设备。录制模块复用同一个设备，避免重复创建。</summary>
    internal static IDirect3DDevice? Device => SharedDevice.Value;

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

    /// <summary>主显示器物理像素边界（录制默认全屏用）。</summary>
    internal static PixelRect GetPrimaryMonitorBounds()
    {
        var width = GetSystemMetrics(SystemMetricCxScreen);
        var height = GetSystemMetrics(SystemMetricCyScreen);

        return width > 0 && height > 0
            ? new PixelRect(0, 0, width, height)
            : new PixelRect(0, 0, 1920, 1080);
    }

    /// <summary>
    /// 找与区域交集面积最大的显示器。MVP 只录单屏：
    /// 选区跨屏时按主屏裁剪，由调用方决定是否提示（多屏拼接见 CaptureRegion）。
    /// </summary>
    internal static (IntPtr Handle, PixelRect Rect)? FindMonitorFor(PixelRect region)
    {
        (IntPtr Handle, PixelRect Rect)? best = null;
        var bestArea = 0L;

        foreach (var monitor in IntersectingMonitors(region))
        {
            if (Intersect(monitor.Rect, region) is not { } overlap)
                continue;

            var area = (long)overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return best;
    }

    /// <summary>
    /// 捕获屏幕物理像素区域（BGRA，top-down）。
    /// 跨多个显示器时逐屏抓取再拼接；任一屏失败返回 null（由调用方回退 GDI）。
    /// </summary>
    public static byte[]? CaptureRegion(int x, int y, int width, int height, bool includeCursor = false)
    {
        if (width <= 0 || height <= 0 || !IsSupported)
            return null;

        var requested = new PixelRect(x, y, width, height);
        var monitors = IntersectingMonitors(requested);

        if (monitors.Count == 0)
            return null;

        // 单屏且完整覆盖：直接裁出请求区域
        if (monitors.Count == 1 && Contains(monitors[0].Rect, requested))
        {
            return CaptureMonitorRegion(
                monitors[0].Handle, monitors[0].Rect, requested, includeCursor);
        }

        // 跨屏：逐屏抓取，按交集拼回请求坐标；没被显示器覆盖的空隙保持黑色（和 GDI 一致）
        var output = new byte[width * 4 * height];

        foreach (var monitor in monitors)
        {
            if (Intersect(monitor.Rect, requested) is not { } region)
                continue;

            var patch = CaptureMonitorRegion(
                monitor.Handle, monitor.Rect, region, includeCursor);

            if (patch is null)
                return null;

            Blit(patch, region, output, width, x, y);
        }

        return output;
    }

    /// <summary>抓一个显示器的一帧，裁出 <paramref name="region"/>（屏幕绝对坐标）。</summary>
    private static byte[]? CaptureMonitorRegion(
        IntPtr monitor,
        PixelRect monitorRect,
        PixelRect region,
        bool includeCursor)
    {
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
            session.IsCursorCaptureEnabled = includeCursor;
            session.StartCapture();

            var crop = new PixelRect(
                region.X - monitorRect.X,
                region.Y - monitorRect.Y,
                region.Width,
                region.Height);

            var deadline = Environment.TickCount64 + FrameTimeoutMilliseconds;

            while (Environment.TickCount64 < deadline)
            {
                using var frame = pool.TryGetNextFrame();
                if (frame is not null)
                    return Extract(frame, crop);

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

    /// <summary>把一块区域拷到整张输出图的对应位置。</summary>
    private static void Blit(
        byte[] patch,
        PixelRect region,
        byte[] output,
        int outputWidth,
        int originX,
        int originY)
    {
        var destX = region.X - originX;
        var destY = region.Y - originY;
        var rowBytes = region.Width * 4;

        for (var row = 0; row < region.Height; row++)
        {
            Buffer.BlockCopy(
                patch,
                row * rowBytes,
                output,
                ((destY + row) * outputWidth + destX) * 4,
                rowBytes);
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

    /// <summary>为指定显示器创建 WGC 捕获项。录制模块复用它建立长会话。</summary>
    internal static GraphicsCaptureItem? CreateItemForMonitor(IntPtr monitor)
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

    /// <summary>为指定窗口创建 WGC 捕获项（窗口录制）。</summary>
    internal static GraphicsCaptureItem? CreateItemForWindow(IntPtr window)
    {
        var interop = GetItemInterop();
        if (interop is null)
            return null;

        var iid = IidGraphicsCaptureItem;

        if (interop.CreateForWindow(window, ref iid, out var item) < 0 || item == IntPtr.Zero)
            return null;

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

    /// <summary>请求区域相交的所有显示器（物理像素）。</summary>
    private static List<(IntPtr Handle, PixelRect Rect)> IntersectingMonitors(PixelRect rect)
    {
        var monitors = new List<(IntPtr, PixelRect)>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT native, IntPtr data) =>
            {
                var bounds = new PixelRect(
                    native.Left,
                    native.Top,
                    native.Right - native.Left,
                    native.Bottom - native.Top);

                if (Intersect(bounds, rect) is not null)
                    monitors.Add((monitor, bounds));

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            monitors.Clear();
        }

        return monitors;
    }

    private static PixelRect? Intersect(PixelRect left, PixelRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);

        return rightEdge <= x || bottomEdge <= y
            ? null
            : new PixelRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width
        && inner.Y + inner.Height <= outer.Y + outer.Height;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    private delegate bool MonitorEnumProc(
        IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
