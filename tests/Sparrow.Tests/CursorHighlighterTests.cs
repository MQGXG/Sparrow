using Shouldly;
using Sparrow.App.Services.Recording;
using Xunit;

namespace Sparrow.Tests;

/// <summary>光标高亮：坐标在画面内时改像素，在画面外时不碰画面。</summary>
public sealed class CursorHighlighterTests
{
    [Fact]
    public void 光标在区域内_会修改中心像素()
    {
        const int width = 100;
        const int height = 100;
        var pixels = new byte[width * height * 4];

        var highlighter = new CursorHighlighter();
        highlighter.DrawAt(pixels, width, height, 50, 50, TimeSpan.Zero);

        var offset = (50 * width + 50) * 4;

        // 琥珀色光圈/圆点：蓝、红通道都应变亮
        pixels[offset].ShouldBeGreaterThan((byte)0);
        pixels[offset + 2].ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void 光标在区域外_不修改像素()
    {
        const int width = 100;
        const int height = 100;
        var pixels = new byte[width * height * 4];

        var highlighter = new CursorHighlighter();
        highlighter.DrawAt(pixels, width, height, -10, 50, TimeSpan.Zero);
        highlighter.DrawAt(pixels, width, height, 50, height + 10, TimeSpan.Zero);

        pixels.Any(value => value != 0).ShouldBeFalse();
    }
}
