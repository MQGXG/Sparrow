using System.Drawing;
using System.Drawing.Imaging;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>只读图片头拿宽高：这是结果列表能画识别框的前提。</summary>
public sealed class ImageSizeReaderTests
{
    [Fact]
    public void 读出PNG的真实尺寸()
    {
        var png = CreatePng(1200, 800);

        var (width, height) = ImageSizeReader.Read(png);

        width.ShouldBe(1200);
        height.ShouldBe(800);
    }

    [Fact]
    public void 空数据与坏数据都返回零()
    {
        ImageSizeReader.Read(ReadOnlyMemory<byte>.Empty).ShouldBe((0, 0));
        ImageSizeReader.Read(new byte[] { 1, 2, 3 }).ShouldBe((0, 0));
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
