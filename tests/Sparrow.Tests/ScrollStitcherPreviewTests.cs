using Shouldly;
using SkiaSharp;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>滚动截图缩略图：按比例缩小，保留左上角像素。</summary>
public sealed class ScrollStitcherPreviewTests
{
    private static byte[] Frame(int width, int height, byte b = 0, byte g = 0, byte r = 0)
    {
        var frame = new byte[width * height * 4];

        for (var index = 0; index + 3 < frame.Length; index += 4)
        {
            frame[index] = b;
            frame[index + 1] = g;
            frame[index + 2] = r;
            frame[index + 3] = 255;
        }

        return frame;
    }

    [Fact]
    public void 没有内容_不生成预览()
    {
        using var stitcher = new ScrollStitcher(10, 10);

        stitcher.BuildPreviewPng(5, 5).ShouldBeNull();
    }

    [Fact]
    public void 预览按比例缩小_并保留像素()
    {
        using var stitcher = new ScrollStitcher(10, 10);

        // 左上角红色，其余黑色
        var frame = Frame(10, 10);
        frame[0] = 0;
        frame[1] = 0;
        frame[2] = 255;
        frame[3] = 255;

        stitcher.Append(frame, out _).ShouldBe(AppendOutcome.Added);

        var png = stitcher.BuildPreviewPng(5, 5).ShouldNotBeNull();

        using var bitmap = SKBitmap.Decode(png);
        bitmap.Width.ShouldBe(5);
        bitmap.Height.ShouldBe(5);

        var corner = bitmap.GetPixel(0, 0);
        corner.Red.ShouldBe((byte)255);
        corner.Green.ShouldBe((byte)0);
    }

    [Fact]
    public void 预览不超过给定尺寸()
    {
        using var stitcher = new ScrollStitcher(20, 20);
        stitcher.Append(Frame(20, 20, g: 128), out _).ShouldBe(AppendOutcome.Added);

        using var bitmap = SKBitmap.Decode(stitcher.BuildPreviewPng(6, 6));

        bitmap.Width.ShouldBeLessThanOrEqualTo(6);
        bitmap.Height.ShouldBeLessThanOrEqualTo(6);
    }
}
