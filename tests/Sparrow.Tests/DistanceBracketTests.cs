using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shouldly;
using SkiaSharp;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>测距与括号：导出几何、方向、45° 吸附、快捷键与标签单位。</summary>
public sealed class DistanceBracketTests
{
    [Fact]
    public void 测距_导出线端刻度和标签()
    {
        var png = CreateWhitePng(200, 200);
        var distance = new DistanceAnnotation(
            new Point(40, 100), new Point(160, 100),
            0xFF000000, 2, "120 px");

        var composed = AnnotationRenderer.Compose(png, [distance], 1);
        using var decoded = SKBitmap.Decode(composed);

        // 线身
        decoded.GetPixel(100, 100).Red.ShouldBeLessThan((byte)120);
        // 端点刻度：线是水平的，刻度应该上下各 4px
        decoded.GetPixel(40, 97).Red.ShouldBeLessThan((byte)120);
        decoded.GetPixel(160, 103).Red.ShouldBeLessThan((byte)120);
        // 标签底在中点上方
        DarkestRed(decoded, 78, 81, 122, 95).ShouldBeLessThan((byte)120);
        // 区域外保持白底
        decoded.GetPixel(10, 10).Red.ShouldBe((byte)255);
    }

    [Fact]
    public void 括号_开口方向决定竖边位置()
    {
        var png = CreateWhitePng(120, 120);
        var right = new BracketAnnotation(new Rect(30, 30, 60, 60), true, 0xFF000000, 2);

        using var rightImage = SKBitmap.Decode(AnnotationRenderer.Compose(png, [right], 1));
        rightImage.GetPixel(30, 60).Red.ShouldBeLessThan((byte)120);   // 左竖边
        rightImage.GetPixel(90, 60).Red.ShouldBe((byte)255);           // 右侧不封口
        rightImage.GetPixel(40, 30).Red.ShouldBeLessThan((byte)120);   // 上横臂

        var left = right with { OpenRight = false };
        using var leftImage = SKBitmap.Decode(AnnotationRenderer.Compose(png, [left], 1));
        leftImage.GetPixel(90, 60).Red.ShouldBeLessThan((byte)120);    // 右竖边
        leftImage.GetPixel(30, 60).Red.ShouldBe((byte)255);
        leftImage.GetPixel(80, 90).Red.ShouldBeLessThan((byte)120);    // 下横臂
    }

    [Fact]
    public void 括号深度_不超过24也不超过短边一半()
    {
        new BracketAnnotation(new Rect(0, 0, 100, 30), true, 0, 2)
            .Depth.ShouldBe(10, 0.01);
        new BracketAnnotation(new Rect(0, 0, 100, 300), true, 0, 2)
            .Depth.ShouldBe(24, 0.01);
        new BracketAnnotation(new Rect(0, 0, 10, 300), true, 0, 2)
            .Depth.ShouldBe(5, 0.01);
    }

    [Fact]
    public void 角度吸附_取最近的45度整数倍()
    {
        var horizontal = AnnotationGeometry.SnapToAngle(
            new Point(0, 0), new Point(100, 10));

        horizontal.Y.ShouldBe(0, 0.001);
        horizontal.X.ShouldBeGreaterThan(99);

        var diagonal = AnnotationGeometry.SnapToAngle(
            new Point(0, 0), new Point(100, 90));

        Math.Abs(diagonal.X - diagonal.Y).ShouldBeLessThan(0.5);
        diagonal.X.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void 截图窗口_测距生成带长度标签的标注()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.D, RawInputModifiers.None, PhysicalKey.D, null);
        window.DistanceToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.WidthGroup.IsVisible.ShouldBeTrue();
        window.DashGroup.IsVisible.ShouldBeTrue();

        // 100 DIP 的水平线；headless 缩放下标签按像素显示
        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 200));
        window.MouseUp(new Point(250, 200), MouseButton.Left);

        var distance = window.Notes[0].ShouldBeOfType<DistanceAnnotation>();
        distance.From.X.ShouldBe(50, 0.01);
        distance.To.X.ShouldBe(150, 0.01);
        distance.Label.ShouldBe("100 px");

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_测距按住Shift吸附到水平()
    {
        var window = CreateWindowWithSelection();
        window.DistanceToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 210), RawInputModifiers.Shift);
        window.MouseUp(new Point(250, 210), MouseButton.Left);

        var distance = window.Notes[0].ShouldBeOfType<DistanceAnnotation>();
        distance.To.Y.ShouldBe(distance.From.Y, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_括号拖拽方向决定开口()
    {
        var window = CreateWindowWithSelection();
        window.BracketToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 从左往右拖 = `[`
        window.MouseDown(new Point(150, 160), MouseButton.Left);
        window.MouseMove(new Point(250, 240));
        window.MouseUp(new Point(250, 240), MouseButton.Left);

        // 从右往左拖 = `]`
        window.MouseDown(new Point(300, 180), MouseButton.Left);
        window.MouseMove(new Point(200, 260));
        window.MouseUp(new Point(200, 260), MouseButton.Left);

        window.Notes.Count.ShouldBe(2);
        window.Notes[0].ShouldBeOfType<BracketAnnotation>().OpenRight.ShouldBeTrue();
        window.Notes[1].ShouldBeOfType<BracketAnnotation>().OpenRight.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_方括号键和ShiftB都能切括号()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.OemOpenBrackets, RawInputModifiers.None, PhysicalKey.None, null);
        window.BracketToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.KeyPress(Key.OemOpenBrackets, RawInputModifiers.None, PhysicalKey.None, null);
        window.BracketToolButton.Classes.Contains("selected").ShouldBeFalse();

        window.KeyPress(Key.B, RawInputModifiers.Shift, PhysicalKey.B, null);
        window.BracketToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.Close();
    }

    private static CaptureWindow CreateWindowWithSelection()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        return window;
    }

    private static byte DarkestRed(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        byte darkest = 255;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var red = bitmap.GetPixel(x, y).Red;
            if (red < darkest)
                darkest = red;
        }

        return darkest;
    }

    private static byte[] CreateWhitePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
