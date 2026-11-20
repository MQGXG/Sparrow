using System.Drawing;
using System.Drawing.Imaging;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 图片解码缓存：列表按 240px 高降采样、详情按 800px 宽降采样、同内容命中缓存、坏图只试一次。
/// 用独立实例而不是 ImageCache.Instance，避免测试之间互相污染。
/// </summary>
public sealed class ImageCacheTests
{
    [AvaloniaFact]
    public void 缩略图按高度降采样而不是解原图()
    {
        var cache = new ImageCache();
        var png = CreatePng(3840, 2160);

        var bitmap = cache.GetThumbnail(png);

        bitmap.ShouldNotBeNull();
        bitmap!.PixelSize.Height.ShouldBeLessThanOrEqualTo(240, "列表只显示 120px 高，解到 240 是 2 倍过采样");
        bitmap.PixelSize.Width.ShouldBeLessThan(3840, "必须真的缩小了，不能还是原尺寸");
    }

    [AvaloniaFact]
    public void 详情图按宽度降采样()
    {
        var cache = new ImageCache();
        var png = CreatePng(3840, 2160);

        var bitmap = cache.GetDetail(png);

        bitmap.ShouldNotBeNull();
        bitmap!.PixelSize.Width.ShouldBeLessThanOrEqualTo(800, "详情在 440 宽的面板里，800 已是 2 倍过采样");
        bitmap.PixelSize.Height.ShouldBeLessThan(2160);
    }

    [AvaloniaFact]
    public void 同一张图只解码一次()
    {
        var cache = new ImageCache();
        var png = CreatePng(1200, 800);

        var first = cache.GetThumbnail(png);
        var second = cache.GetThumbnail(png);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("同内容必须命中缓存");

        var stats = cache.Stats();
        stats.ThumbnailEntries.ShouldBe(1);
    }

    [AvaloniaFact]
    public void 内容相同但引用不同也要命中()
    {
        var cache = new ImageCache();

        // Refresh() 每次重建 VM、重新读文件，同一条图片每次都是新的 byte[] 实例；
        // 按引用缓存会永远不命中，所以必须按内容哈希
        var first = cache.GetThumbnail(CreatePng(640, 480));
        var second = cache.GetThumbnail(CreatePng(640, 480));

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("内容相同就要命中，哪怕数组是新实例");
    }

    [AvaloniaFact]
    public void 坏图只试一次就记入失败集()
    {
        var cache = new ImageCache();
        var broken = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 };

        cache.GetThumbnail(broken).ShouldBeNull();
        cache.GetThumbnail(broken).ShouldBeNull();

        var stats = cache.Stats();
        stats.Failed.ShouldBe(1, "同一张坏图不该每次渲染都重试解码");
    }

    [AvaloniaFact]
    public void 缓存超上限时按字节淘汰()
    {
        var cache = new ImageCache();

        // 多塞几张，触发 16MB 上限（240px 高的图约 400KB，需要几十张）
        // seed 必须不同：同尺寸同颜色的 PNG 字节完全一样，内容哈希会相同，永远只缓存 1 张
        for (var i = 0; i < 80; i++)
            cache.GetThumbnail(CreatePng(1600, 900, seed: i)).ShouldNotBeNull();

        var stats = cache.Stats();
        stats.ThumbnailBytes.ShouldBeLessThanOrEqualTo(16L * 1024 * 1024, "缓存必须有字节上限");
        stats.ThumbnailEntries.ShouldBeLessThan(80, "超出上限要淘汰最老的");
    }

    [AvaloniaFact]
    public void 原尺寸解码不进缓存()
    {
        var cache = new ImageCache();
        var png = CreatePng(1920, 1080);

        var bitmap = ImageCache.DecodeOriginal(png);

        bitmap.ShouldNotBeNull();
        bitmap!.PixelSize.Width.ShouldBe(1920, "灯箱要看原始细节，不能降采样");
        bitmap.Dispose();

        cache.Stats().ThumbnailEntries.ShouldBe(0);
        cache.Stats().DetailEntries.ShouldBe(0);
    }

    /// <summary>生成一张指定尺寸的 PNG；seed 不同内容就不同（否则内容哈希会相同，测不出缓存上限）。</summary>
    private static byte[] CreatePng(int width, int height, int seed = 0)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(255, seed & 0xFF, (seed * 7) & 0xFF, 0x40));

            // 画一个位置随 seed 变化的方块，保证每张图字节不同
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, seed % width, seed % height, 8, 8);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
