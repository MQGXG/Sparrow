using System.Runtime.InteropServices;
using Avalonia;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 录制画面的光标高亮：琥珀色光圈 + 点击波纹。
///
/// 直接改 WGC 的 BGRA 缓冲区（top-down，stride = width * 4）。
/// 系统的实际光标仍由 WGC 的 IsCursorCaptureEnabled 画进去，
/// 这里只叠加“看得见光标在哪”的高亮，不替代光标本身。
/// </summary>
internal sealed class CursorHighlighter
{
    private const int VkLeftButton = 0x01;
    private const double RippleMilliseconds = 380;

    private readonly List<(TimeSpan Time, int X, int Y)> _ripples = [];
    private readonly int _radius;
    private readonly byte _blue;
    private readonly byte _green;
    private readonly byte _red;
    private readonly byte _alpha;
    private readonly bool _shadow;
    private bool _wasDown;

    public CursorHighlighter(
        int radius = 22,
        uint color = 0xFFFFD640,
        byte opacity = 210,
        bool shadow = false)
    {
        _radius = Math.Clamp(radius, 6, 90);
        _blue = (byte)(color & 0xFF);
        _green = (byte)((color >> 8) & 0xFF);
        _red = (byte)((color >> 16) & 0xFF);
        _alpha = opacity;
        _shadow = shadow;
    }

    public void Draw(byte[] pixels, int width, int height, PixelRect screenRect, TimeSpan now)
    {
        if (!GetCursorPos(out var cursor))
            return;

        var down = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

        if (down && !_wasDown)
        {
            _ripples.Add((
                now,
                cursor.X - screenRect.X,
                cursor.Y - screenRect.Y));
        }

        _wasDown = down;

        DrawAt(
            pixels,
            width,
            height,
            cursor.X - screenRect.X,
            cursor.Y - screenRect.Y,
            now);
    }

    /// <summary>在指定画面坐标绘制光标与波纹。抽出来便于单元测试。</summary>
    public void DrawAt(byte[] pixels, int width, int height, int x, int y, TimeSpan now)
    {
        // 光标在录制区域外时不画光圈，但波纹保留（可能在边界附近）。
        if (x >= 0 && y >= 0 && x < width && y < height)
        {
            if (_shadow)
            {
                DrawRing(
                    pixels,
                    width,
                    height,
                    x,
                    y,
                    _radius + 4,
                    3,
                    0,
                    0,
                    0,
                    (byte)(_alpha / 2));
            }

            DrawRing(pixels, width, height, x, y, _radius, 2, _blue, _green, _red, _alpha);

            DrawDisc(
                pixels,
                width,
                height,
                x,
                y,
                3,
                _blue,
                _green,
                _red,
                (byte)Math.Min(_alpha + 20, 255));
        }

        foreach (var ripple in _ripples)
        {
            var age = (now - ripple.Time).TotalMilliseconds;

            if (age > RippleMilliseconds)
                continue;

            var progress = age / RippleMilliseconds;
            var radius = 12 + (int)(progress * 34);
            var alpha = (byte)(210 * (1 - progress));

            DrawRing(pixels, width, height, ripple.X, ripple.Y, radius, 3, 255, 255, 255, alpha);
        }

        _ripples.RemoveAll(ripple =>
            (now - ripple.Time).TotalMilliseconds > RippleMilliseconds);
    }

    private static void DrawRing(
        byte[] pixels,
        int width,
        int height,
        int cx,
        int cy,
        int radius,
        int thickness,
        byte b,
        byte g,
        byte r,
        byte a)
    {
        var inner = radius - thickness / 2.0;
        var outer = radius + thickness / 2.0;
        var limit = radius + thickness;

        for (var y = cy - limit; y <= cy + limit; y++)
        {
            if (y < 0 || y >= height)
                continue;

            for (var x = cx - limit; x <= cx + limit; x++)
            {
                if (x < 0 || x >= width)
                    continue;

                var dx = x - cx;
                var dy = y - cy;
                var distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance < inner || distance > outer)
                    continue;

                BlendPixel(pixels, (y * width + x) * 4, b, g, r, a);
            }
        }
    }

    private static void DrawDisc(
        byte[] pixels,
        int width,
        int height,
        int cx,
        int cy,
        int radius,
        byte b,
        byte g,
        byte r,
        byte a)
    {
        var square = radius * radius;

        for (var y = cy - radius; y <= cy + radius; y++)
        {
            if (y < 0 || y >= height)
                continue;

            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (x < 0 || x >= width)
                    continue;

                var dx = x - cx;
                var dy = y - cy;

                if (dx * dx + dy * dy > square)
                    continue;

                BlendPixel(pixels, (y * width + x) * 4, b, g, r, a);
            }
        }
    }

    private static void BlendPixel(byte[] pixels, int offset, byte b, byte g, byte r, byte a)
    {
        pixels[offset] = Blend(pixels[offset], b, a);
        pixels[offset + 1] = Blend(pixels[offset + 1], g, a);
        pixels[offset + 2] = Blend(pixels[offset + 2], r, a);
    }

    private static byte Blend(byte background, byte foreground, byte alpha) =>
        (byte)((foreground * alpha + background * (255 - alpha)) / 255);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
