using Shouldly;
using SkiaSharp;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>滚动长图的头尾裁剪：纵向裁行、横向裁列。</summary>
public sealed class ScrollImageTrimmerTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.CadetBlue);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void 裁剪头尾_只保留中间行()
    {
        var png = Png(4, 10);

        var trimmed = ScrollImageTrimmer.TrimPng(png, trimStart: 2, trimEnd: 3);

        using var decoded = SKBitmap.Decode(trimmed);
        decoded.Width.ShouldBe(4);
        decoded.Height.ShouldBe(5);
    }

    [Fact]
    public void 不裁剪_原样返回()
    {
        var png = Png(4, 10);

        ScrollImageTrimmer.TrimPng(png, 0, 0).ShouldBeSameAs(png);
    }

    [Fact]
    public void 裁过头_至少保留一行()
    {
        var png = Png(4, 10);

        var trimmed = ScrollImageTrimmer.TrimPng(png, trimStart: 8, trimEnd: 8);

        using var decoded = SKBitmap.Decode(trimmed);
        decoded.Height.ShouldBe(1);
    }

    [Fact]
    public void 横向裁剪_只保留中间列()
    {
        var png = Png(10, 4);

        var trimmed = ScrollImageTrimmer.TrimPng(
            png, trimStart: 2, trimEnd: 3, horizontal: true);

        using var decoded = SKBitmap.Decode(trimmed);
        decoded.Width.ShouldBe(5);
        decoded.Height.ShouldBe(4);
    }

    [Fact]
    public void 横向裁过头_至少保留一列()
    {
        var png = Png(10, 4);

        var trimmed = ScrollImageTrimmer.TrimPng(
            png, trimStart: 8, trimEnd: 8, horizontal: true);

        using var decoded = SKBitmap.Decode(trimmed);
        decoded.Width.ShouldBe(1);
        decoded.Height.ShouldBe(4);
    }

    [Fact]
    public void 非法数据_原样返回()
    {
        var junk = new byte[] { 1, 2, 3 };

        ScrollImageTrimmer.TrimPng(junk, 1, 1).ShouldBeSameAs(junk);
    }
}
