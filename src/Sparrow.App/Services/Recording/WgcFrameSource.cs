using System.Runtime.InteropServices;
using Avalonia;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 一帧 BGRA 像素（紧凑排列，stride = Width * 4，top-down）。
/// 像素数组由帧源复用，调用方必须在下一次取帧前写完。
/// </summary>
internal readonly record struct VideoFrame(
    byte[] Pixels,
    int Width,
    int Height,
    TimeSpan Timestamp);

/// <summary>
/// 常驻 WGC 帧源：一次建会话，连续取帧。
///
/// 与 <see cref="WgcCapture.CaptureRegion"/> 的区别：后者是「抓一帧就销毁会话」，
/// 每帧都要重建 item / pool / session，30fps 下时间会全花在初始化上。
/// 这里会话只建一次，帧池留 3 帧缓冲；后台线程没有消息泵，
/// 所以用轮询 <c>TryGetNextFrame</c>，不依赖 FrameArrived 事件（不会触发）。
/// </summary>
internal sealed class WgcFrameSource : IDisposable
{
    /// <summary>帧池缓冲数。太小会丢帧，太大会增加延迟；3 帧是采样与延迟的折中。</summary>
    private const int PoolBufferCount = 3;

    private readonly IDirect3DDevice _device;
    private readonly PixelRect _crop;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels;

    private Direct3D11CaptureFramePool _pool;
    private GraphicsCaptureSession _session;
    private int _frameWidth;
    private int _frameHeight;
    private TimeSpan? _firstTimestamp;
    private bool _disposed;

    private WgcFrameSource(
        IDirect3DDevice device,
        Direct3D11CaptureFramePool pool,
        GraphicsCaptureSession session,
        PixelRect crop,
        int width,
        int height,
        int frameWidth,
        int frameHeight,
        PixelRect screenRect)
    {
        _device = device;
        _pool = pool;
        _session = session;
        _crop = crop;
        _width = width;
        _height = height;
        _pixels = new byte[width * 4 * height];
        ScreenRect = screenRect;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
    }

    /// <summary>实际录制的屏幕物理矩形（用于把鼠标坐标换算到画面坐标）。</summary>
    public PixelRect ScreenRect { get; }

    /// <summary>目标区域宽度（物理像素）。</summary>
    public int Width => _width;

    /// <summary>目标区域高度（物理像素）。</summary>
    public int Height => _height;

    /// <summary>
    /// 为指定窗口创建帧源（窗口录制）。
    /// 窗口内容由 WGC 直接捕获，不会被其他窗口遮挡；窗口尺寸变化时
    /// 帧池不会自动重建，当前策略是保持开始时的尺寸。
    /// </summary>
    public static WgcFrameSource? TryCreateForWindow(
        IntPtr window,
        bool includeCursor,
        out string? error)
    {
        error = null;

        if (!WgcCapture.IsSupported)
        {
            error = "当前系统不支持 Windows Graphics Capture（需要 Windows 10 2004 / build 19041 以上）。";
            return null;
        }

        if (window == IntPtr.Zero || !IsWindow(window))
        {
            error = "窗口已关闭或句柄无效。";
            return null;
        }

        if (WgcCapture.Device is not { } device)
        {
            error = "无法创建 Direct3D 设备，屏幕录制不可用。";
            return null;
        }

        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            var item = WgcCapture.CreateItemForWindow(window);
            if (item is null)
            {
                error = "无法捕获该窗口（可能受保护或已最小化）。";
                return null;
            }

            var width = item.Size.Width & ~1;
            var height = item.Size.Height & ~1;

            if (width <= 0 || height <= 0)
            {
                error = "窗口尺寸无效。";
                return null;
            }

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolBufferCount,
                item.Size);

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = includeCursor;
            TryDisableCaptureBorder(session);
            session.StartCapture();

            return new WgcFrameSource(
                device,
                pool,
                session,
                new PixelRect(0, 0, width, height),
                width,
                height,
                item.Size.Width,
                item.Size.Height,
                GetClientScreenRect(window));
        }
        catch (Exception ex)
        {
            session?.Dispose();
            pool?.Dispose();
            error = $"启动窗口捕获失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 关闭 WGC 的捕获边框：Windows 会在被捕获区域外围画一圈黄色高亮，
    /// 表示「正在被录制」。Win11 22H2+ 可以用 IsBorderRequired 关掉；
    /// 老系统没有这个属性时保留系统默认（属隐私指示器，不算 bug）。
    /// </summary>
    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            // TargetFramework 是 19041，编译期没有这个属性；
            // Win11 22H2+ 的运行时投影才带 IsBorderRequired，用反射避免升级 SDK。
            var property = session.GetType().GetProperty("IsBorderRequired");
            property?.SetValue(session, false);
        }
        catch
        {
            // 属性不存在（旧 SDK / 旧系统）或系统拒绝：保持默认边框。
        }
    }

    /// <summary>窗口客户区在屏幕上的物理矩形（光标坐标换算用）。</summary>
    private static PixelRect GetClientScreenRect(IntPtr window)
    {
        if (!GetClientRect(window, out var client))
            return default;

        var origin = new NativePoint { X = 0, Y = 0 };

        if (!ClientToScreen(window, ref origin))
            return default;

        return new PixelRect(
            origin.X,
            origin.Y,
            client.Right - client.Left,
            client.Bottom - client.Top);
    }

    /// <summary>为目标区域创建帧源。失败时返回 null，并给出可展示的原因。</summary>
    public static WgcFrameSource? TryCreate(
        PixelRect region,
        bool includeCursor,
        out string? error)
    {
        error = null;

        if (!WgcCapture.IsSupported)
        {
            error = "当前系统不支持 Windows Graphics Capture（需要 Windows 10 2004 / build 19041 以上）。";
            return null;
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            error = "录制区域为空。";
            return null;
        }

        if (WgcCapture.Device is not { } device)
        {
            error = "无法创建 Direct3D 设备，屏幕录制不可用。";
            return null;
        }

        if (WgcCapture.FindMonitorFor(region) is not { } monitor)
        {
            error = "录制区域不在任何显示器上。";
            return null;
        }

        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            var item = WgcCapture.CreateItemForMonitor(monitor.Handle);
            if (item is null)
            {
                error = "无法创建屏幕捕获项（WGC）。";
                return null;
            }

            // 选区可能跨出显示器：先裁到目标显示器内，避免取帧时越界。
            if (Intersect(region, monitor.Rect) is not { } visible
                || visible.Width <= 0
                || visible.Height <= 0)
            {
                error = "录制区域与显示器没有交集。";
                return null;
            }

            var crop = new PixelRect(
                visible.X - monitor.Rect.X,
                visible.Y - monitor.Rect.Y,
                visible.Width,
                visible.Height);

            // NV12 按 2x2 采样 UV：奇数宽高会让最后一列/行越界，这里裁成偶数。
            var width = crop.Width & ~1;
            var height = crop.Height & ~1;

            if (width <= 0 || height <= 0)
            {
                error = "录制区域太小（至少需要 2×2 像素）。";
                return null;
            }

            // CreateFreeThreaded：录制线程没有 DispatcherQueue，事件模型不可用。
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolBufferCount,
                item.Size);

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = includeCursor;
            TryDisableCaptureBorder(session);
            session.StartCapture();

            var screenRect = new PixelRect(
                monitor.Rect.X + crop.X,
                monitor.Rect.Y + crop.Y,
                width,
                height);

            return new WgcFrameSource(
                device,
                pool,
                session,
                crop,
                width,
                height,
                item.Size.Width,
                item.Size.Height,
                screenRect);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            pool?.Dispose();
            error = $"启动屏幕捕获失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 取下一帧。没有新帧时返回 false（调用方可以复用上一帧）。
    /// 时间戳相对第一帧，用于写视频的 PTS。
    /// </summary>
    public bool TryGetFrame(out VideoFrame frame)
    {
        frame = default;

        if (_disposed)
            return false;

        Direct3D11CaptureFrame? captured = null;

        try
        {
            captured = _pool.TryGetNextFrame();
            if (captured is null)
                return false;

            // 窗口被缩放：重建帧池，保持输出尺寸不变（新画面裁到左上角、不足补黑）。
            var size = captured.ContentSize;

            if (size.Width != _frameWidth || size.Height != _frameHeight)
                RecreatePool(size);

            var timestamp = captured.SystemRelativeTime;
            _firstTimestamp ??= timestamp;

            if (!CopyPixels(captured.Surface))
                return false;

            frame = new VideoFrame(
                _pixels,
                _width,
                _height,
                timestamp - _firstTimestamp.Value);

            return true;
        }
        catch
        {
            // 单帧失败不终止录制：显示器切换/尺寸变化时 WGC 会短暂给不出帧。
            return false;
        }
        finally
        {
            captured?.Dispose();
        }
    }

    /// <summary>窗口尺寸变化时重建帧池；失败则保留旧池，下一帧再试。</summary>
    private void RecreatePool(Windows.Graphics.SizeInt32 size)
    {
        try
        {
            _pool.Recreate(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolBufferCount,
                size);

            _frameWidth = size.Width;
            _frameHeight = size.Height;

            // 尺寸变化后旧画面会残留在右下角，先补黑；CopyPixels 再覆盖可见部分。
            Array.Clear(_pixels);
        }
        catch
        {
            // 重建失败：继续用旧帧池。
        }
    }

    /// <summary>把 surface 裁到当前区域并拷进复用的 byte[]。</summary>
    private bool CopyPixels(IDirect3DSurface surface)
    {
        using var bitmap = SoftwareBitmap
            .CreateCopyFromSurfaceAsync(surface)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (bitmap is null)
            return false;

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var description = buffer.GetPlaneDescription(0);

        var copyWidth = Math.Min(_width, Math.Max(0, description.Width - _crop.X));
        var copyHeight = Math.Min(_height, Math.Max(0, description.Height - _crop.Y));

        if (copyWidth <= 0 || copyHeight <= 0)
            return false;

        var reference = buffer.CreateReference();

        try
        {
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var source, out _);

            var sourceRowBytes = copyWidth * 4;
            var targetStride = _width * 4;

            for (var row = 0; row < copyHeight; row++)
            {
                var offset = description.StartIndex
                             + (_crop.Y + row) * description.Stride
                             + _crop.X * 4;

                Marshal.Copy(source + offset, _pixels, row * targetStride, sourceRowBytes);
            }

            // 新画面比开始尺寸小：右侧/底部补黑，避免残留上一帧。
            if (copyWidth < _width)
            {
                for (var row = 0; row < copyHeight; row++)
                {
                    Array.Clear(
                        _pixels,
                        row * targetStride + copyWidth * 4,
                        (_width - copyWidth) * 4);
                }
            }

            if (copyHeight < _height)
            {
                Array.Clear(
                    _pixels,
                    copyHeight * targetStride,
                    (_height - copyHeight) * targetStride);
            }

            return true;
        }
        finally
        {
            if (reference is IDisposable disposable)
                disposable.Dispose();
        }
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 先停会话再释放帧池，避免捕获回调在已释放的池上取帧。
        _session.Dispose();
        _pool.Dispose();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
