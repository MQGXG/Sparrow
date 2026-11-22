using Shouldly;
using SkiaSharp;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 滚动截图拼接算法。
/// 用合成帧（每行颜色不同）模拟「页面向下滚动」，断言拼接后的行数。
/// 不启动窗口、不截屏，纯算法回归。
/// </summary>
public sealed class ScrollStitcherTests
{
    private const int Width = 40;
    private const int FrameHeight = 100;

    /// <summary>造一张高图，每行都有唯一特征。</summary>
    private static byte[] Canvas(int height)
    {
        var stride = Width * 4;
        var bytes = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = y * stride + x * 4;
                bytes[i] = (byte)(y % 256);
                bytes[i + 1] = (byte)((x * 3) % 256);
                bytes[i + 2] = (byte)((y * 7) % 256);
                bytes[i + 3] = 255;
            }
        }

        return bytes;
    }

    private static byte[] Frame(byte[] canvas, int offset)
    {
        var stride = Width * 4;
        var frame = new byte[stride * FrameHeight];
        Array.Copy(canvas, offset * stride, frame, 0, frame.Length);
        return frame;
    }

    [Fact]
    public void 连续滚动_按新增部分拼接()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out var first).ShouldBe(AppendOutcome.Added);
        first.ShouldBe(FrameHeight);

        // 每次向下滚动 35 像素
        stitcher.Append(Frame(canvas, 35), out var second).ShouldBe(AppendOutcome.Added);
        second.ShouldBe(35);

        stitcher.Append(Frame(canvas, 70), out var third).ShouldBe(AppendOutcome.Added);
        third.ShouldBe(35);

        stitcher.Append(Frame(canvas, 105), out var fourth).ShouldBe(AppendOutcome.Added);
        fourth.ShouldBe(35);

        stitcher.TotalRows.ShouldBe(FrameHeight + 35 * 3);
    }

    [Fact]
    public void 页面没滚动_不重复追加()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);
        stitcher.Append(Frame(canvas, 0), out var added).ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
        stitcher.TotalRows.ShouldBe(FrameHeight);
    }

    [Fact]
    public void 滚动超过一屏_停止跟踪()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);

        // 滚动 150 像素，超过帧高，没有任何重叠
        stitcher.Append(Frame(canvas, 150), out _).ShouldBe(AppendOutcome.LostTracking);
        stitcher.TotalRows.ShouldBe(FrameHeight);
    }

    [Fact]
    public void 纯色页面_视为没有滚动()
    {
        // 纯白页面所有行哈希相同，匹配会落在帧底；
        // 应该判成 Duplicate，而不是误拼一大段。
        var white = new byte[Width * 4 * FrameHeight];
        Array.Fill(white, (byte)0xFF);

        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(white, out _).ShouldBe(AppendOutcome.Added);
        stitcher.Append(white, out var added).ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
    }

    [Fact]
    public void 拼出的PNG尺寸等于累计行数()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out _);
        stitcher.Append(Frame(canvas, 40), out _);

        var png = stitcher.BuildPng();

        png.ShouldNotBeNull();

        using var bitmap = SKBitmap.Decode(png);
        bitmap.Width.ShouldBe(Width);
        bitmap.Height.ShouldBe(FrameHeight + 40);
    }

    [Fact]
    public void 流式编码的像素与原图一致()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out _);
        stitcher.Append(Frame(canvas, 40), out _);

        var png = stitcher.BuildPng();
        png.ShouldNotBeNull();
        using var bitmap = SKBitmap.Decode(png);

        var y = FrameHeight + 39;
        var offset = y * Width * 4 + 7 * 4;
        var color = bitmap.GetPixel(7, y);

        color.Blue.ShouldBe(canvas[offset]);
        color.Green.ShouldBe(canvas[offset + 1]);
        color.Red.ShouldBe(canvas[offset + 2]);
        color.Alpha.ShouldBe((byte)255);
    }

    [Fact]
    public void 超出内存预算后转存磁盘_结果仍然正确()
    {
        var canvas = Canvas(400);
        var directory = Path.Combine(
            Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using (var stitcher = new ScrollStitcher(Width, FrameHeight, memoryBudget: 1000, directory))
            {
                stitcher.Append(Frame(canvas, 0), out _);

                stitcher.UsesDisk.ShouldBeTrue("超出预算后应落盘，而不是继续占内存");

                stitcher.Append(Frame(canvas, 40), out _);

                var png = stitcher.BuildPng();
                png.ShouldNotBeNull();
                using var bitmap = SKBitmap.Decode(png);

                bitmap.Width.ShouldBe(Width);
                bitmap.Height.ShouldBe(FrameHeight + 40);

                var y = FrameHeight + 39;
                var offset = y * Width * 4 + 7 * 4;
                var color = bitmap.GetPixel(7, y);

                color.Blue.ShouldBe(canvas[offset]);
                color.Green.ShouldBe(canvas[offset + 1]);
                color.Red.ShouldBe(canvas[offset + 2]);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
