using Shouldly;
using SkiaSharp;
using Sparrow.App.Services.Recording;
using Xunit;

namespace Sparrow.Tests;

/// <summary>图文水印：空布局不画、文字/图片按相对布局落到画面上。</summary>
public sealed class WatermarkRendererTests
{
    [Fact]
    public void 空布局_不修改画面()
    {
        var pixels = new byte[200 * 100 * 4];

        using var renderer = new WatermarkRenderer(new WatermarkLayout());
        renderer.Draw(pixels, 200, 100);

        pixels.Any(value => value != 0).ShouldBeFalse();
    }

    [Fact]
    public void 文字水印_在指定位置写入像素()
    {
        var pixels = new byte[200 * 100 * 4];

        var layout = new WatermarkLayout
        {
            Text = "TEST",
            CenterX = 0.5,
            CenterY = 0.5,
            Scale = 0.2,
            Opacity = 1
        };

        using var renderer = new WatermarkRenderer(layout);
        renderer.Draw(pixels, 200, 100);

        pixels.Any(value => value != 0).ShouldBeTrue();
    }

    [Fact]
    public void 图片水印_使用图片路径绘制()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sparrow-wm-{Guid.NewGuid():N}.png");

        try
        {
            using (var bitmap = new SKBitmap(10, 10))
            {
                bitmap.Erase(SKColors.Red);

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            }

            var pixels = new byte[200 * 100 * 4];

            var layout = new WatermarkLayout
            {
                ImagePath = path,
                CenterX = 0.5,
                CenterY = 0.5,
                Scale = 0.2,
                Opacity = 1
            };

            using var renderer = new WatermarkRenderer(layout);
            renderer.Draw(pixels, 200, 100);

            pixels.Any(value => value != 0).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
