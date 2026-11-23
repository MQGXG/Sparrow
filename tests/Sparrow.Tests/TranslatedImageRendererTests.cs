using Avalonia;
using Shouldly;
using SkiaSharp;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>译文图渲染：按行盖掉原文、写译文，框外保持原样。</summary>
public class TranslatedImageRendererTests
{
    [Fact]
    public void 没有译文时返回空()
    {
        using var crop = new SKBitmap(100, 50);

        var png = TranslatedImageRenderer.Render(crop, 1, [
            new TranslatedImageRenderer.LineBox(new Rect(10, 10, 60, 20), "abc", null)
        ]);

        png.ShouldBeNull();
    }

    [Fact]
    public void 有译文时在行框里写字_框外不变()
    {
        using var crop = WhiteBitmap(200, 100);

        var png = TranslatedImageRenderer.Render(crop, 1, [
            new TranslatedImageRenderer.LineBox(new Rect(10, 10, 120, 24), "原文", "HELLO")
        ]);

        png.ShouldNotBeNull();

        using var decoded = SKBitmap.Decode(png);
        decoded.Width.ShouldBe(200);
        decoded.Height.ShouldBe(100);

        // 行框内出现深色像素（文字被画上去了）
        HasInk(decoded, 11, 11, 129, 33).ShouldBeTrue();
        // 框外保持白底
        ((int)decoded.GetPixel(190, 90).Red).ShouldBeGreaterThan(200);
    }

    [Fact]
    public void 长译文会缩小字号_不越出框()
    {
        using var crop = WhiteBitmap(120, 60);

        var png = TranslatedImageRenderer.Render(crop, 1, [
            new TranslatedImageRenderer.LineBox(
                new Rect(5, 5, 60, 20), "原文", "A very long translation that must shrink")
        ]);

        png.ShouldNotBeNull();

        using var decoded = SKBitmap.Decode(png);
        // 框右侧之外不该被写字（如果字号没缩，会溢出）
        HasInk(decoded, 70, 5, 119, 59).ShouldBeFalse();
    }

    [Fact]
    public void 对比图_左右并排且按缝分隔()
    {
        using var left = WhiteBitmap(40, 20);
        using (var c = new SKCanvas(left)) c.Clear(SKColors.Red);
        using var right = WhiteBitmap(30, 20);
        using (var c = new SKCanvas(right)) c.Clear(SKColors.Blue);

        var png = TranslatedImageRenderer.BuildCompare(left, right, gap: 10);

        png.ShouldNotBeNull();
        using var decoded = SKBitmap.Decode(png);
        decoded.Width.ShouldBe(80);   // 40 + 10 + 30
        decoded.Height.ShouldBe(20);

        ((int)decoded.GetPixel(5, 5).Red).ShouldBeGreaterThan(200);     // 左：原文
        ((int)decoded.GetPixel(55, 5).Blue).ShouldBeGreaterThan(200);   // 右：译文（左 40 + 缝 10 之后）
    }

    [Fact]
    public void 从结果文本里取出译文段()
    {
        var text = "原文第一行\n原文第二行\n\n—— 译文（English）——\n\nTranslated";

        CaptureWindow.ExtractTranslatedText(text).ShouldBe("Translated");
        CaptureWindow.ExtractTranslatedText("只有原文").ShouldBeNull();
    }

    private static SKBitmap WhiteBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        return bitmap;
    }

    private static bool HasInk(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        for (var y = Math.Max(0, top); y < Math.Min(bitmap.Height, bottom); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(bitmap.Width, right); x++)
            {
                if (bitmap.GetPixel(x, y).Red < 120)
                    return true;
            }
        }

        return false;
    }
}
