using System.Runtime.InteropServices;
using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>
/// 屏幕区域截图（Windows）。
///
/// 用 GDI 的 BitBlt 直接抓屏幕，不经过 Avalonia 的渲染层——
/// 因为要截的是「屏幕上真实显示的内容」，不是某个窗口的视觉树。
/// </summary>
public static class ScreenCapture
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    /// <summary>截取屏幕物理像素区域，返回 PNG 字节。失败返回 null。</summary>
    public static byte[]? Capture(int x, int y, int width, int height)
    {
        var pixels = CaptureRaw(x, y, width, height);
        return pixels is null ? null : EncodePng(pixels, width, height);
    }

    /// <summary>
    /// 截取原始 BGRA 像素（top-down，stride = width*4）。
    /// 滚动截图每 200ms 抓一帧，跳过 PNG 编解码。
    /// </summary>
    public static byte[]? CaptureRaw(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy))
                return null;

            return ReadPixels(memoryDc, bitmap, width, height);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>等画面稳定后截取 PNG；用于冻结失败/滚动截图的实时路径。</summary>
    public static byte[]? CaptureStable(int x, int y, int width, int height)
    {
        var pixels = CaptureStableRaw(x, y, width, height);
        return pixels is null ? null : EncodePng(pixels, width, height);
    }

    /// <summary>
    /// 等画面稳定后抓原始 BGRA：连续两帧一致才返回，最多 <paramref name="attempts"/> 轮。
    /// 用于「先冻结再框选」——避免滚动惯性/重绘延迟抓到上一帧。
    /// </summary>
    public static byte[]? CaptureStableRaw(
        int x,
        int y,
        int width,
        int height,
        int attempts = 8,
        int intervalMs = 50)
    {
        byte[]? previous = null;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var current = CaptureRaw(x, y, width, height);
            if (current is null)
                return null;

            if (previous is not null && current.AsSpan().SequenceEqual(previous))
                return current;

            previous = current;

            if (attempt < attempts - 1)
                Thread.Sleep(intervalMs);
        }

        // 持续动画（视频等）永远不稳定：用最后一帧
        return previous;
    }

    /// <summary>从整屏 BGRA 里裁剪一块；越界或参数非法返回 null。</summary>
    public static byte[]? Crop(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0)
            return null;

        if (x + width > sourceWidth || y + height > sourceHeight)
            return null;

        var sourceStride = sourceWidth * 4;
        var targetStride = width * 4;
        var result = new byte[targetStride * height];

        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source,
                (y + row) * sourceStride + x * 4,
                result,
                row * targetStride,
                targetStride);
        }

        return result;
    }

    private static byte[]? ReadPixels(IntPtr memoryDc, IntPtr bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // 负数 = top-down，和 SKBitmap 的行顺序一致
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(stride * height)
            }
        };

        var lines = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
        return lines == 0 ? null : pixels;
    }

    /// <summary>BGRA 像素转 PNG（alpha 固定不透明）。</summary>
    public static byte[]? EncodePng(byte[] pixels, int width, int height)
    {
        var stride = width * 4;
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var skBitmap = new SKBitmap(imageInfo);
        Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFO bmi, uint usage);
}
