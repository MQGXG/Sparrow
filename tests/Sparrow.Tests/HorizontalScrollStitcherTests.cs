using Shouldly;
using SkiaSharp;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 横向滚动截图拼接算法。
/// 用合成帧（每列颜色不同）模拟「页面向右滚动」，断言拼接后的列数。
/// 不启动窗口、不截屏，纯算法回归。
/// </summary>
public sealed class HorizontalScrollStitcherTests
{
    /// <summary>选区宽度：一帧可见的列数。</summary>
    private const int FrameWidth = 100;

    /// <summary>选区高度：结果的高度。</summary>
    private const int Height = 40;

    /// <summary>源图的宽度，要比一帧宽得多，才能模拟横向滚动。</summary>
    private const int CanvasWidth = 600;

    /// <summary>造一张很宽的图，每列都有唯一特征。</summary>
    private static byte[] Canvas()
    {
        var stride = CanvasWidth * 4;
        var bytes = new byte[stride * Height];

        for (var x = 0; x < CanvasWidth; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                var i = y * stride + x * 4;
                bytes[i] = (byte)(x % 256);
                bytes[i + 1] = (byte)((y * 3) % 256);
                bytes[i + 2] = (byte)((x * 7) % 256);
                bytes[i + 3] = 255;
            }
        }

        return bytes;
    }

    /// <summary>从源图的 offset 列开始截一帧。</summary>
    private static byte[] Frame(byte[] canvas, int offset)
    {
        var stride = FrameWidth * 4;
        var frame = new byte[stride * Height];

        for (var x = 0; x < FrameWidth; x++)
        {
            var sourceColumn = offset + x;

            for (var y = 0; y < Height; y++)
            {
                var source = y * CanvasWidth * 4 + sourceColumn * 4;
                var target = y * stride + x * 4;

                frame[target] = canvas[source];
                frame[target + 1] = canvas[source + 1];
                frame[target + 2] = canvas[source + 2];
                frame[target + 3] = 255;
            }
        }

        return frame;
    }

    /// <summary>
    /// 造一帧：左右固定栏不动，中间内容从 canvas 的 offset 列开始滚动。
    /// </summary>
    private static byte[] FrameWithBars(
        byte[] canvas, int offset, int leftColumns, int rightColumns)
    {
        var stride = FrameWidth * 4;
        var frame = new byte[stride * Height];
        var contentColumns = FrameWidth - leftColumns - rightColumns;

        for (var x = 0; x < contentColumns; x++)
        {
            var sourceColumn = offset + x;

            for (var y = 0; y < Height; y++)
            {
                var source = y * CanvasWidth * 4 + sourceColumn * 4;
                var target = y * stride + (leftColumns + x) * 4;

                frame[target] = canvas[source];
                frame[target + 1] = canvas[source + 1];
                frame[target + 2] = canvas[source + 2];
                frame[target + 3] = 255;
            }
        }

        // 固定栏每列特征都不同，避免被「纯色段无信息」过滤掉
        for (var x = 0; x < leftColumns; x++)
            PaintFixedColumn(frame, x, 0xFD, 3);

        for (var x = FrameWidth - rightColumns; x < FrameWidth; x++)
            PaintFixedColumn(frame, x, 0xFE, 11);

        return frame;
    }

    /// <summary>把某一列改成灰色，模拟帧间渲染差异。</summary>
    private static void RecolorColumn(byte[] frame, int x)
    {
        var stride = FrameWidth * 4;

        for (var y = 0; y < Height; y++)
        {
            var i = y * stride + x * 4;
            frame[i] = 0x7F;
            frame[i + 1] = 0x7F;
            frame[i + 2] = 0x7F;
            frame[i + 3] = 255;
        }
    }

    private static void PaintFixedColumn(byte[] frame, int x, byte tag, int factor)
    {
        var stride = FrameWidth * 4;

        for (var y = 0; y < Height; y++)
        {
            var i = y * stride + x * 4;
            frame[i] = tag;
            frame[i + 1] = (byte)((y * 5) % 256);
            frame[i + 2] = (byte)((x * factor) % 256);
            frame[i + 3] = 255;
        }
    }

    [Fact]
    public void 重叠区用新帧覆盖_避免接缝竖线()
    {
        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);

        // 第二帧向右滚 30 列，同时把重叠区的一列改成灰色，模拟两帧之间的
        // 亚像素 / 抗锯齿差异。只追加新增列时，这一列会留在旧帧里形成接缝。
        var next = Frame(canvas, 30);
        RecolorColumn(next, 50);

        stitcher.Append(next, out _).ShouldBe(AppendOutcome.Added);

        var png = stitcher.BuildPng();
        png.ShouldNotBeNull();

        using var bitmap = SKBitmap.Decode(png);

        // 新帧 x=50 对应页面第 80 列；覆盖生效后画布第 80 列应来自新帧
        var pixel = bitmap.GetPixel(80, 0);
        ((int)pixel.Red).ShouldBe(0x7F);
        ((int)pixel.Green).ShouldBe(0x7F);
        ((int)pixel.Blue).ShouldBe(0x7F);
    }

    [Fact]
    public void 连续向右滚动_按新增列拼接()
    {
        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(Frame(canvas, 0), out var first).ShouldBe(AppendOutcome.Added);
        first.ShouldBe(FrameWidth);

        // 每次向右滚动 25 列
        stitcher.Append(Frame(canvas, 25), out var second).ShouldBe(AppendOutcome.Added);
        second.ShouldBe(25);

        stitcher.Append(Frame(canvas, 50), out var third).ShouldBe(AppendOutcome.Added);
        third.ShouldBe(25);

        stitcher.TotalRows.ShouldBe(FrameWidth + 25 * 2);
    }

    [Fact]
    public void 页面没滚动_不重复追加()
    {
        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);
        stitcher.Append(Frame(canvas, 0), out var added).ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
        stitcher.TotalRows.ShouldBe(FrameWidth);
    }

    [Fact]
    public void 固定左栏_滚动时仍能继续拼接()
    {
        const int leftColumns = 40;
        const int step = 20;

        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(FrameWithBars(canvas, 0, leftColumns, 0), out var first)
            .ShouldBe(AppendOutcome.Added);

        first.ShouldBe(FrameWidth);

        // 固定左栏不参与结果，内容区右侧继续追加
        for (var round = 1; round <= 4; round++)
        {
            stitcher.Append(
                    FrameWithBars(canvas, round * step, leftColumns, 0),
                    out var added)
                .ShouldBe(AppendOutcome.Added, $"第 {round} 次滚动应继续拼接");

            added.ShouldBe(step);
        }

        // 没有固定右栏，首帧 100 列全部保留，再追加 4 × 20 列
        stitcher.TotalRows.ShouldBe(FrameWidth + step * 4);
    }

    [Fact]
    public void 固定右栏_滚动时仍能继续拼接()
    {
        const int rightColumns = 40;
        const int step = 20;

        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(FrameWithBars(canvas, 0, 0, rightColumns), out var first)
            .ShouldBe(AppendOutcome.Added);

        first.ShouldBe(FrameWidth);

        // 固定右栏以前会让右缘模板一直命中原位，从第二次开始停止
        for (var round = 1; round <= 4; round++)
        {
            stitcher.Append(
                    FrameWithBars(canvas, round * step, 0, rightColumns),
                    out var added)
                .ShouldBe(AppendOutcome.Added, $"第 {round} 次滚动应继续拼接");

            added.ShouldBe(step);
        }

        // 首帧 100 列，校准后裁掉 40 列固定右栏，再追加 4 × 20 列
        stitcher.TotalRows.ShouldBe(60 + step * 4);
    }

    [Fact]
    public void 固定左右栏_页面没滚动仍判重复()
    {
        const int leftColumns = 20;
        const int rightColumns = 30;

        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(FrameWithBars(canvas, 0, leftColumns, rightColumns), out _)
            .ShouldBe(AppendOutcome.Added);

        stitcher.Append(FrameWithBars(canvas, 0, leftColumns, rightColumns), out var added)
            .ShouldBe(AppendOutcome.Duplicate);

        added.ShouldBe(0);
        stitcher.TotalRows.ShouldBe(FrameWidth);
    }

    [Fact]
    public void 输出图片宽度等于总列数()
    {
        var canvas = Canvas();
        var stitcher = new HorizontalScrollStitcher(FrameWidth, Height);

        stitcher.Append(Frame(canvas, 0), out _).ShouldBe(AppendOutcome.Added);
        stitcher.Append(Frame(canvas, 30), out _).ShouldBe(AppendOutcome.Added);

        var png = stitcher.BuildPng();
        png.ShouldNotBeNull();

        using var bitmap = SKBitmap.Decode(png);

        bitmap.ShouldNotBeNull();
        bitmap.Width.ShouldBe(stitcher.TotalRows);
        bitmap.Height.ShouldBe(Height);
    }
}
