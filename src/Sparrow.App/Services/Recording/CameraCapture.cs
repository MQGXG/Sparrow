using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 摄像头采集（MediaCapture + MediaFrameReader，CPU 帧）。
///
/// 只保留最新一帧：录制合成和预览都读同一份缓冲，不排队、不追帧。
/// 初始化是异步的，失败只记 <see cref="Error"/>，不阻断屏幕录制。
/// </summary>
internal sealed class CameraCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private MediaCapture? _media;
    private MediaFrameReader? _reader;
    private byte[] _pixels = [];
    private byte[] _previewPixels = [];
    private byte[] _previewSnapshot = [];
    private int _width;
    private int _height;
    private int _previewWidth;
    private int _previewHeight;
    private int _started;
    private bool _disposed;

    /// <summary>是否已经拿到过至少一帧。</summary>
    public bool HasFrame
    {
        get
        {
            lock (_gate)
                return _width > 0 && _height > 0 && _pixels.Length > 0;
        }
    }

    /// <summary>初始化/采集失败原因；成功后为 null。</summary>
    public string? Error { get; private set; }

    public static CameraCapture Create() => new();

    /// <summary>
    /// 在后台完成设备枚举与初始化。调用方可以立即开始录制，
    /// 预览和合成在有帧之后自动出现。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(StartAsync);
    }

    private async Task StartAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            var device = devices.FirstOrDefault(item => item.IsEnabled) ?? devices.FirstOrDefault();

            if (device is null)
            {
                Error = "没有可用的摄像头设备。";
                return;
            }

            var media = new MediaCapture();

            await media.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });

            if (_disposed)
            {
                media.Dispose();
                return;
            }

            var source = media.FrameSources.Values.FirstOrDefault(
                item => item.Info?.SourceKind == MediaFrameSourceKind.Color);

            if (source is null)
            {
                media.Dispose();
                Error = "摄像头没有可用的彩色视频源。";
                return;
            }

            // 画中画不需要改采集分辨率：预览端会缩小后再拷给 UI，
            // 录制端直接使用设备默认帧；避免部分驱动在 SetFormatAsync 上卡住。
            MediaFrameReader reader;

            try
            {
                reader = await media.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            }
            catch
            {
                // 设备不支持直接给 BGRA：拿默认格式，逐帧转换。
                reader = await media.CreateFrameReaderAsync(source);
            }

            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;

            await reader.StartAsync();

            lock (_gate)
            {
                if (_disposed)
                {
                    reader.FrameArrived -= OnFrameArrived;
                    reader.Dispose();
                    media.Dispose();
                    return;
                }

                _media = media;
                _reader = reader;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;

            if (bitmap is null)
                return;

            SoftwareBitmap? converted = null;

            try
            {
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                    || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                {
                    converted = SoftwareBitmap.Convert(
                        bitmap,
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied);

                    bitmap = converted;
                }

                CopyFrame(bitmap);
            }
            finally
            {
                converted?.Dispose();
            }
        }
        catch
        {
            // 单帧失败不终止采集：设备切换/休眠时会短暂出错。
        }
    }

    private void CopyFrame(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var description = buffer.GetPlaneDescription(0);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;

        if (width <= 0 || height <= 0)
            return;

        var rowBytes = width * 4;
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var source, out _);

        lock (_gate)
        {
            if (_disposed)
                return;

            if (_pixels.Length != rowBytes * height)
                _pixels = new byte[rowBytes * height];

            for (var row = 0; row < height; row++)
            {
                var offset = description.StartIndex + row * description.Stride;

                Marshal.Copy(source + offset, _pixels, row * rowBytes, rowBytes);
            }

            _width = width;
            _height = height;
            BuildPreview(width, height);
        }
    }

    /// <summary>
    /// 生成 640 宽以内的预览帧。预览窗口很小，没必要每次把整张 1080p/720p
    /// 拷到 UI 线程；这里在采集线程做最邻近缩小，UI 每帧只搬几十 KB。
    /// </summary>
    private void BuildPreview(int width, int height)
    {
        var previewWidth = Math.Min(width, 640);
        var previewHeight = Math.Max(1, (int)Math.Round((double)height * previewWidth / width));

        if (_previewWidth != previewWidth
            || _previewHeight != previewHeight
            || _previewPixels.Length != previewWidth * previewHeight * 4)
        {
            _previewWidth = previewWidth;
            _previewHeight = previewHeight;
            _previewPixels = new byte[previewWidth * previewHeight * 4];
        }

        var sourceStride = width * 4;
        var targetStride = previewWidth * 4;

        for (var y = 0; y < previewHeight; y++)
        {
            var sourceRow = (int)((long)y * height / previewHeight) * sourceStride;
            var targetRow = y * targetStride;

            for (var x = 0; x < previewWidth; x++)
            {
                var sourceOffset = sourceRow + (int)((long)x * width / previewWidth) * 4;
                var targetOffset = targetRow + x * 4;

                _previewPixels[targetOffset] = _pixels[sourceOffset];
                _previewPixels[targetOffset + 1] = _pixels[sourceOffset + 1];
                _previewPixels[targetOffset + 2] = _pixels[sourceOffset + 2];
                _previewPixels[targetOffset + 3] = _pixels[sourceOffset + 3];
            }
        }
    }

    /// <summary>已有帧的尺寸；没有帧时返回 false。</summary>
    public bool TryGetFrameSize(out int width, out int height)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            return width > 0 && height > 0 && _pixels.Length > 0;
        }
    }

    /// <summary>
    /// 把最新一帧合成到录制的 BGRA 缓冲区（top-down，stride = destWidth * 4）。
    /// 按 UniformToFill 裁剪，保持画面比例；带圆角与细边框。
    /// </summary>
    public void Draw(
        byte[] destination,
        int destWidth,
        int destHeight,
        PixelRect rect,
        bool mirror)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        lock (_gate)
        {
            if (_pixels.Length == 0 || _width <= 0 || _height <= 0)
                return;

            if (destination.Length < destWidth * destHeight * 4)
                return;

            var sourceHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            try
            {
                using var image = SKImage.FromPixels(
                    new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    sourceHandle.AddrOfPinnedObject(),
                    _width * 4);

                using var surface = SKSurface.Create(
                    new SKImageInfo(destWidth, destHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                    destinationHandle.AddrOfPinnedObject(),
                    destWidth * 4);

                if (surface is null)
                    return;

                var canvas = surface.Canvas;
                var target = new SKRect(
                    rect.X,
                    rect.Y,
                    rect.Right,
                    rect.Bottom);

                var source = CropToFill(_width, _height, rect.Width, rect.Height);
                var radius = Math.Max(4f, Math.Min(rect.Width, rect.Height) * 0.055f);

                using var clip = new SKPath();
                clip.AddRoundRect(target, radius, radius);

                using var paint = new SKPaint { IsAntialias = true };

                canvas.Save();
                canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);

                if (mirror)
                {
                    // 围绕目标矩形中线镜像。
                    canvas.Translate(target.Left + target.Right, 0);
                    canvas.Scale(-1, 1);
                }

                canvas.DrawImage(image, source, target, paint);
                canvas.Restore();

                using var border = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, radius * 0.18f),
                    Color = new SKColor(255, 255, 255, 120)
                };

                canvas.DrawRoundRect(target, radius, radius, border);
                canvas.Flush();
            }
            finally
            {
                sourceHandle.Free();
                destinationHandle.Free();
            }
        }
    }

    /// <summary>把摄像头帧拷进预览用的 WriteableBitmap；尺寸不匹配时返回 false。</summary>
    /// <summary>预览缓冲尺寸（480p 以内）。</summary>
    public bool TryGetPreviewSize(out int width, out int height)
    {
        lock (_gate)
        {
            width = _previewWidth;
            height = _previewHeight;
            return width > 0 && height > 0 && _previewPixels.Length > 0;
        }
    }

    /// <summary>把缩小后的预览帧拷进 WriteableBitmap。</summary>
    public bool CopyFrameTo(WriteableBitmap bitmap)
    {
        int width;
        int height;

        // 先在采集锁内做一次快照，随后释放锁再碰 Avalonia 位图。
        // 否则 UI 线程等渲染线程时可能反过来卡住录制线程的 Draw。
        lock (_gate)
        {
            if (_previewPixels.Length == 0 || _previewWidth <= 0 || _previewHeight <= 0)
                return false;

            if (bitmap.PixelSize.Width != _previewWidth
                || bitmap.PixelSize.Height != _previewHeight)
                return false;

            if (_previewSnapshot.Length != _previewPixels.Length)
                _previewSnapshot = new byte[_previewPixels.Length];

            Buffer.BlockCopy(_previewPixels, 0, _previewSnapshot, 0, _previewPixels.Length);
            width = _previewWidth;
            height = _previewHeight;
        }

        try
        {
            using var buffer = bitmap.Lock();
            var rowBytes = width * 4;

            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    _previewSnapshot,
                    row * rowBytes,
                    buffer.Address + row * buffer.RowBytes,
                    rowBytes);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>UniformToFill：裁掉源画面多出来的部分，避免拉伸变形。</summary>
    private static SKRect CropToFill(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var sourceAspect = (double)sourceWidth / sourceHeight;
        var targetAspect = (double)targetWidth / targetHeight;

        if (Math.Abs(sourceAspect - targetAspect) < 0.001)
            return new SKRect(0, 0, sourceWidth, sourceHeight);

        if (sourceAspect > targetAspect)
        {
            var width = sourceHeight * targetAspect;
            var left = (sourceWidth - width) / 2;
            return new SKRect((float)left, 0, (float)(left + width), sourceHeight);
        }

        var height = sourceWidth / targetAspect;
        var top = (sourceHeight - height) / 2;
        return new SKRect(0, (float)top, sourceWidth, (float)(top + height));
    }

    public void Dispose()
    {
        MediaFrameReader? reader;
        MediaCapture? media;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            reader = _reader;
            media = _media;
            _reader = null;
            _media = null;
        }

        _cts.Cancel();

        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // 设备已经被拔出。
        }

        reader?.Dispose();
        media?.Dispose();
        _cts.Dispose();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
