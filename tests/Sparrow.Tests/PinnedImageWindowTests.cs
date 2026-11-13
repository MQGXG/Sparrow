using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>贴图窗口的滚轮缩放。</summary>
public sealed class PinnedImageWindowTests
{
    // 1×1 透明 PNG，只用于让 Bitmap 能解码
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [AvaloniaFact]
    public void 滚轮向上_放大窗口()
    {
        var window = new PinnedImageWindow(OnePixelPng);
        window.Show();

        var width = window.Width;
        window.MouseWheel(new Point(10, 10), new Vector(0, 1));

        window.Width.ShouldBeGreaterThan(width);

        window.Close();
    }

    [AvaloniaFact]
    public void 缩放不超过上下限()
    {
        var window = new PinnedImageWindow(OnePixelPng);
        window.Show();

        var width = window.Width;

        for (var i = 0; i < 40; i++)
            window.MouseWheel(new Point(10, 10), new Vector(0, 1));

        window.Width.ShouldBe(width * 2, 0.01);

        for (var i = 0; i < 40; i++)
            window.MouseWheel(new Point(10, 10), new Vector(0, -1));

        window.Width.ShouldBe(width * 0.5, 0.01);

        window.Close();
    }
}
