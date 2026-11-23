using System.Runtime.InteropServices;
using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>截图取帧后端。</summary>
public enum CaptureApi
{
    /// <summary>WGC 可用就优先，失败回退 GDI。</summary>
    Auto,

    /// <summary>只尝试 WGC（失败仍会回退 GDI，保证截图不中断）。</summary>
    Wgc,

    /// <summary>只用 GDI BitBlt。</summary>
    Gdi
}

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

    /// <summary>取帧后端：自动 / WGC / GDI。</summary>
    public static CaptureApi Api { get; set; } = CaptureApi.Auto;

    /// <summary>普通截图是否画上鼠标光标；滚动截图始终关掉，避免每帧重复。</summary>
    public static bool ShowCursor { get; set; }

    /// <summary>是否还原放大镜全屏颜色滤镜（仅 GDI 路径；WGC 本来就是原始像素）。</summary>
    public static bool RestoreColors { get; set; } = true;

    /// <summary>截取屏幕物理像素区域，返回 PNG 字节。失败返回 null。</summary>
    public static byte[]? Capture(int x, int y, int width, int height)
    {
        var pixels = CaptureRaw(x, y, width, height);
        return pixels is null ? null : EncodePng(pixels, width, height);
    }

    /// <summary>
    /// 截取原始 BGRA 像素（top-down，stride = width*4）。
    /// 自动 / WGC 模式先试 Windows Graphics Capture；跨屏或不支持时回退 GDI。
    /// </summary>
    public static byte[]? CaptureRaw(
        int x,
        int y,
        int width,
        int height,
        byte[]? reuse = null,
        bool? includeCursor = null)
    {
        if (width <= 0 || height <= 0)
            return null;

        var withCursor = includeCursor ?? ShowCursor;
        var useWgc = Api == CaptureApi.Wgc
                     || (Api == CaptureApi.Auto && HdrDisplay.AnyEnabled);

        if (useWgc && WgcCapture.IsSupported)
        {
            var captured = WgcCapture.CaptureRegion(x, y, width, height, withCursor);
            if (captured is not null)
                return captured;
        }

        return CaptureRawGdi(x, y, width, height, reuse, withCursor);
    }

    private static byte[]? CaptureRawGdi(
        int x,
        int y,
        int width,
        int height,
        byte[]? reuse,
        bool includeCursor)
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

            // 放大镜滤镜：先还原像素，再画光标（光标本身不受滤镜影响）
            var transform = RestoreColors ? MagnifierColorEffect.Current : null;

            if (transform is not null)
            {
                var filtered = ReadPixels(memoryDc, bitmap, width, height, reuse);
                if (filtered is null)
                    return null;

                transform.Apply(filtered);

                if (!WritePixels(memoryDc, bitmap, width, height, filtered))
                    return null;
            }

            if (includeCursor)
                DrawCursor(memoryDc, x, y);

            return ReadPixels(memoryDc, bitmap, width, height, reuse);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把当前鼠标光标画到内存 DC 上（坐标换算到捕获区域）。</summary>
    private static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };

        if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0)
            return;

        if (!GetIconInfo(info.hCursor, out var icon))
            return;

        try
        {
            var x = info.ptScreenPos.X - originX - icon.xHotspot;
            var y = info.ptScreenPos.Y - originY - icon.yHotspot;

            DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero)
                DeleteObject(icon.hbmColor);

            if (icon.hbmMask != IntPtr.Zero)
                DeleteObject(icon.hbmMask);
        }
    }

    /// <summary>等画面稳定后截取 PNG；用于冻结失败/滚动截图的实时路径。</summary>
    public static byte[]? CaptureStable(
        int x,
        int y,
        int width,
        int height,
        bool? includeCursor = null)
    {
        var pixels = CaptureStableRaw(x, y, width, height, includeCursor: includeCursor);
        return pixels is null ? null : EncodePng(pixels, width, height);
    }

    /// <summary>
    /// 等画面稳定后抓原始 BGRA：抓两帧比较，几乎一致就用后一帧，最多 2 轮。
    /// 用于「先冻结再框选」——避免滚动惯性/重绘延迟抓到上一帧。
    ///
    /// 不做逐字节相等：任务栏时钟、光标闪烁这类几十个像素的变化永远等不到，
    /// 结果就是每轮都跑满、白分配几百 MB。允许千分之一的像素不同。
    /// 最多只保留两块缓冲（比较用），不会按轮次累积大对象。
    /// </summary>
    public static byte[]? CaptureStableRaw(
        int x,
        int y,
        int width,
        int height,
        int attempts = 2,
        int intervalMs = 40,
        bool? includeCursor = null)
    {
        var length = width * 4 * height;
        var maxDifferentPixels = Math.Max(64, length / 4 / 1000);
        byte[]? previous = null;
        byte[]? spare = null;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var current = CaptureRaw(x, y, width, height, spare, includeCursor);
            if (current is null)
                return null;

            if (previous is not null && NearlyEqual(current, previous, maxDifferentPixels))
                return current;

            spare = previous;
            previous = current;

            if (attempt < attempts - 1)
                Thread.Sleep(intervalMs);
        }

        // 持续动画（视频等）永远不稳定：用最后一帧
        return previous;
    }

    /// <summary>比较 BGRA 两帧，允许最多 <paramref name="maxDifferentPixels"/> 个像素不同（忽略 alpha）。</summary>
    private static bool NearlyEqual(byte[] current, byte[] previous, int maxDifferentPixels)
    {
        if (current.Length != previous.Length)
            return false;

        var different = 0;

        for (var index = 0; index + 3 < current.Length; index += 4)
        {
            if (current[index] == previous[index] &&
                current[index + 1] == previous[index + 1] &&
                current[index + 2] == previous[index + 2])
            {
                continue;
            }

            if (++different > maxDifferentPixels)
                return false;
        }

        return true;
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

    private static byte[]? ReadPixels(
        IntPtr memoryDc,
        IntPtr bitmap,
        int width,
        int height,
        byte[]? reuse)
    {
        var stride = width * 4;
        var length = stride * height;
        var pixels = reuse is not null && reuse.Length == length ? reuse : new byte[length];

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

    /// <summary>把像素写回内存 DC（还原滤镜后、画光标前）。</summary>
    private static bool WritePixels(
        IntPtr memoryDc,
        IntPtr bitmap,
        int width,
        int height,
        byte[] pixels)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(width * 4 * height)
            }
        };

        return SetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) != 0;
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

    [DllImport("gdi32.dll")]
    private static extern int SetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFO bmi, uint usage);

    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        IntPtr hdc,
        int x,
        int y,
        IntPtr icon,
        int width,
        int height,
        int animationStep,
        IntPtr flickerFreeBrush,
        int flags);
}
