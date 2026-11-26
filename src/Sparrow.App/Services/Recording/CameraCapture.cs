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

    /// <summary>每收到一帧自增。预览用它跳过没有新帧的刷新。</summary>
    private long _frameVersion;

    private long _fpsWindowStart;
    private int _fpsFrames;

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

    /// <summary>最新一帧的版本号；预览据此判断是否真的需要刷新。</summary>
    public long FrameVersion
    {
        get
        {
            lock (_gate)
                return _frameVersion;
        }
    }

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

            // 默认格式可能是 15fps（弱光下还会更低）：画中画会明显卡顿。
            // 仅在默认帧率偏低时尝试切到 30fps、≤720p 的格式；
            // 个别驱动会在 SetFormatAsync 上卡住，所以加超时并保留默认格式兜底。
            App.Log.Info($"[camera] 默认采集格式：{DescribeFormat(source.CurrentFormat)}");
            await TryUseBetterFormatAsync(source);

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

    /// <summary>
    /// 默认帧率低于 20fps、或分辨率超过 1080p 时，挑一个 30fps、宽度 ≤1280 的格式。
    /// 画中画尺寸很小，720p 足够；分辨率降下来后每帧拷贝和缩放也更快。
    /// </summary>
    private static async Task TryUseBetterFormatAsync(MediaFrameSource source)
    {
        try
        {
            var current = source.CurrentFormat;
            var currentFps = FrameRate(current);
            var currentWidth = current?.VideoFormat?.Width ?? 0;

            if (currentFps >= 20 && currentWidth <= 1920)
                return;

            MediaFrameFormat? Pick(int maxWidth) => source.SupportedFormats
                .Where(format => format.VideoFormat is { Width: > 0, Height: > 0 })
                .Where(format => FrameRate(format) >= 24)
                .Where(format => format.VideoFormat.Width <= maxWidth)
                .OrderByDescending(FrameRate)
                .ThenByDescending(format => (long)format.VideoFormat.Width * format.VideoFormat.Height)
                .FirstOrDefault();

            var best = Pick(1280) ?? Pick(1920) ?? Pick(int.MaxValue);

            if (best is null)
            {
                App.Log.Warn("[camera] 没有找到 ≥24fps 的采集格式，继续用默认格式");
                return;
            }

            await source.SetFormatAsync(best).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            App.Log.Info($"[camera] 采集格式切换到 {DescribeFormat(best)}");
        }
        catch (TimeoutException)
        {
            App.Log.Warn("[camera] 切换采集格式超时，继续用默认格式");
        }
        catch (Exception ex)
        {
            App.Log.Warn($"[camera] 切换采集格式失败：{ex.Message}");
        }
    }

    private static double FrameRate(MediaFrameFormat? format)
    {
        var rate = format?.FrameRate;

        if (rate is null || rate.Denominator == 0)
            return 0;

        return (double)rate.Numerator / rate.Denominator;
    }

    private static string DescribeFormat(MediaFrameFormat? format)
    {
        if (format is null)
            return "未知";

        var video = format.VideoFormat;

        return $"{video?.Width ?? 0}x{video?.Height ?? 0}@{FrameRate(format):0.#}fps";
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var source = frame?.VideoMediaFrame?.SoftwareBitmap;

            if (source is null)
                return;

            SoftwareBitmap? converted = null;

            try
            {
                var bitmap = source;

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
                LogFrameRate();
            }
            finally
            {
                converted?.Dispose();

                // 帧自带的 SoftwareBitmap 是独立强引用：只 Dispose MediaFrameReference
                // 不会释放它。不显式释放会让可用帧缓冲越来越少（官方文档明确要求）。
                source.Dispose();
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

            if (description.Stride == rowBytes)
            {
                // 大多数摄像头是连续内存：一次拷完，省掉每行一次 P/Invoke。
                Marshal.Copy(source + description.StartIndex, _pixels, 0, rowBytes * height);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    var offset = description.StartIndex + row * description.Stride;

                    Marshal.Copy(source + offset, _pixels, row * rowBytes, rowBytes);
                }
            }

            _width = width;
            _height = height;
            BuildPreview(width, height);
            _frameVersion++;
        }
    }

    /// <summary>每 5 秒记录一次真实采集帧率，方便确认“慢”是不是设备/弱光导致的掉帧。</summary>
    private void LogFrameRate()
    {
        var count = Interlocked.Increment(ref _fpsFrames);
        var now = Environment.TickCount64;
        var start = Interlocked.Read(ref _fpsWindowStart);

        if (start == 0)
        {
            Interlocked.CompareExchange(ref _fpsWindowStart, now, 0);
            return;
        }

        var elapsed = now - start;

        if (elapsed < 5000)
            return;

        if (Interlocked.CompareExchange(ref _fpsWindowStart, now, start) != start)
            return;

        Interlocked.Exchange(ref _fpsFrames, 0);
        App.Log.Info($"[camera] 实际采集 {count * 1000.0 / elapsed:0.#}fps");
    }

    /// <summary>
    /// 生成 640 宽以内的预览帧。预览窗口很小，没必要每次把整张 1080p/720p
    /// 拷到 UI 线程；这里在采集线程做缩小，UI 每帧只搬几十 KB。
    /// 用 Skia 的原生缩放，避免 C# 逐像素循环在采集回调里占满 CPU。
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

        var sourceHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        var targetHandle = GCHandle.Alloc(_previewPixels, GCHandleType.Pinned);

        try
        {
            using var sourceBitmap = new SKBitmap();
            using var targetBitmap = new SKBitmap();

            sourceBitmap.InstallPixels(
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                sourceHandle.AddrOfPinnedObject(),
                width * 4);

            targetBitmap.InstallPixels(
                new SKImageInfo(previewWidth, previewHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                targetHandle.AddrOfPinnedObject(),
                previewWidth * 4);

            sourceBitmap.ScalePixels(
                targetBitmap,
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        finally
        {
            sourceHandle.Free();
            targetHandle.Free();
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
