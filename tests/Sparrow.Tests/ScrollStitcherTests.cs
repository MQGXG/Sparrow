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

    /// <summary>造一帧：内容从 canvas 的 offset 行开始，底部 footerRows 行固定不动。</summary>
    private static byte[] FrameWithFooter(byte[] canvas, int offset, int footerRows)
    {
        var stride = Width * 4;
        var frame = new byte[stride * FrameHeight];
        var contentHeight = FrameHeight - footerRows;

        Array.Copy(canvas, offset * stride, frame, 0, contentHeight * stride);

        // 底栏每行特征都不同，避免被「纯色段无信息」过滤掉
        for (var y = contentHeight; y < FrameHeight; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = y * stride + x * 4;
                frame[i] = 0xFE;
                frame[i + 1] = (byte)(y % 256);
                frame[i + 2] = (byte)((y * 11) % 256);
                frame[i + 3] = 255;
            }
        }

        return frame;
    }

    /// <summary>
    /// 造一帧：顶部 headerRows 行固定页眉，底部 footerRows 行固定底栏，
    /// 中间内容从 canvas 的 offset 行开始滚动（真实网页最常见的布局）。
    /// </summary>
    private static byte[] FrameWithHeaderFooter(
        byte[] canvas, int offset, int headerRows, int footerRows)
    {
        var stride = Width * 4;
        var frame = new byte[stride * FrameHeight];
        var contentRows = FrameHeight - headerRows - footerRows;

        Array.Copy(canvas, offset * stride, frame, headerRows * stride, contentRows * stride);

        // 页眉 / 底栏都做成每行特征不同，避免被「纯色段」规则跳过
        for (var y = 0; y < headerRows; y++)
            PaintFixedRow(frame, y, 0xFD, 3);

        for (var y = FrameHeight - footerRows; y < FrameHeight; y++)
            PaintFixedRow(frame, y, 0xFE, 11);

        return frame;
    }

    private static void PaintFixedRow(byte[] frame, int y, byte tag, int factor)
    {
        var stride = Width * 4;

        for (var x = 0; x < Width; x++)
        {
            var i = y * stride + x * 4;
            frame[i] = tag;
            frame[i + 1] = (byte)(y % 256);
            frame[i + 2] = (byte)((y * factor) % 256);
            frame[i + 3] = 255;
        }
    }

    /// <summary>把某一行改成灰色，模拟光标闪烁 / 局部动画。</summary>
    private static void RecolorRow(byte[] frame, int y)
    {
        var stride = Width * 4;

        for (var x = 0; x < Width; x++)
        {
            var i = y * stride + x * 4;
            frame[i] = 0x7F;
            frame[i + 1] = 0x7F;
            frame[i + 2] = 0x7F;
            frame[i + 3] = 255;
        }
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
    public void 固定底栏_滚动时仍能继续拼接()
    {
        const int footerRows = 40;
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(FrameWithFooter(canvas, 0, footerRows), out var first)
            .ShouldBe(AppendOutcome.Added);
        first.ShouldBe(FrameHeight);

        // 连续 4 次向下滚 30 行：固定底栏以前会让帧底模板一直命中原位，
        // 从第二次开始就被判成 Duplicate / LostTracking。
        for (var step = 1; step <= 4; step++)
        {
            stitcher.Append(FrameWithFooter(canvas, step * 30, footerRows), out var added)
                .ShouldBe(AppendOutcome.Added, $"第 {step} 次滚动应继续拼接");

            added.ShouldBe(30);
        }

        // 首帧 100 行，校准后裁掉 40 行固定底栏，再追加 4 × 30 行
        stitcher.TotalRows.ShouldBe(60 + 30 * 4);
    }

    [Fact]
    public void 固定页眉和底栏_小选区滚动时仍能继续拼接()
    {
        // 常见网页布局：顶部导航固定、底部工具栏固定，中间只剩 40 行内容。
        // 小选区下重叠区很窄，之前固定区域投出的 0 票会压掉内容票。
        const int headerRows = 20;
        const int footerRows = 40;
        const int step = 20;

        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(FrameWithHeaderFooter(canvas, 0, headerRows, footerRows), out var first)
            .ShouldBe(AppendOutcome.Added);
        first.ShouldBe(FrameHeight);

        for (var round = 1; round <= 4; round++)
        {
            stitcher.Append(
                    FrameWithHeaderFooter(canvas, round * step, headerRows, footerRows),
                    out var added)
                .ShouldBe(AppendOutcome.Added, $"第 {round} 次滚动应继续拼接");

            added.ShouldBe(step);
        }

        // 首帧 100 行，裁掉 40 行固定底栏后是 60 行，再追加 4 × 20 行
        stitcher.TotalRows.ShouldBe(60 + step * 4);
    }

    [Fact]
    public void 固定页眉和底栏_页面没滚动仍判重复()
    {
        const int headerRows = 20;
        const int footerRows = 40;

        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(FrameWithHeaderFooter(canvas, 0, headerRows, footerRows), out _)
            .ShouldBe(AppendOutcome.Added);

        stitcher.Append(FrameWithHeaderFooter(canvas, 0, headerRows, footerRows), out var added)
            .ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
        stitcher.TotalRows.ShouldBe(FrameHeight);
    }

    [Fact]
    public void 固定底栏_页面没滚动仍判重复()
    {
        const int footerRows = 40;
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(FrameWithFooter(canvas, 0, footerRows), out _)
            .ShouldBe(AppendOutcome.Added);

        stitcher.Append(FrameWithFooter(canvas, 0, footerRows), out var added)
            .ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
        stitcher.TotalRows.ShouldBe(FrameHeight);
    }

    [Fact]
    public void 局部动画_少量行不同仍能继续拼接()
    {
        var canvas = Canvas(400);
        var stitcher = new ScrollStitcher(Width, FrameHeight);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);

        // 滚动 35 行，同时有 5 行在变化（光标 / 局部重绘）。
        // 帧底模板会有几行对不上，需要多段投票接手。
        var next = Frame(canvas, 35);
        foreach (var y in new[] { 30, 40, 50, 60, 70 })
            RecolorRow(next, y);

        stitcher.Append(next, out var added).ShouldBe(AppendOutcome.Added);
        added.ShouldBe(35);
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
