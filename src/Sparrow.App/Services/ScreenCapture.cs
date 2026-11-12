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
    private static byte[]? EncodePng(byte[] pixels, int width, int height)
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
