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

/// <summary>角度：夹角计算、圆弧/标签导出、两步拖拽与草稿优先级。</summary>
public sealed class AngleTests
{
    [Fact]
    public void 角度_垂直边得到90度_共线得到180度()
    {
        AnnotationGeometry.AngleBetween(
            new Point(0, 0), new Point(100, 0), new Point(0, 100))
            .ShouldBe(90, 0.01);

        AnnotationGeometry.AngleBetween(
            new Point(0, 0), new Point(-100, 0), new Point(100, 0))
            .ShouldBe(180, 0.01);
    }

    [Fact]
    public void 角度_退化边不会产生NaN()
    {
        var value = AnnotationGeometry.AngleBetween(
            new Point(0, 0), new Point(0, 0), new Point(0, 100));

        value.ShouldBe(0);
        double.IsNaN(value).ShouldBeFalse();
    }

    [Fact]
    public void 角度_导出两条边圆弧和标签()
    {
        var png = CreateWhitePng(200, 200);
        var angle = new AngleAnnotation(
            new Point(100, 150), new Point(160, 150), new Point(100, 90),
            0xFF000000, 2, "90°");

        using var decoded = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [angle], 1));

        // 两条射线
        decoded.GetPixel(130, 150).Red.ShouldBeLessThan((byte)120);
        decoded.GetPixel(100, 120).Red.ShouldBeLessThan((byte)120);

        // 半径 36 的圆弧经过 -45° 方向
        var arcX = 100 + 36 * Math.Cos(-Math.PI / 4);
        var arcY = 150 + 36 * Math.Sin(-Math.PI / 4);
        DarkestRed(decoded,
            (int)arcX - 2, (int)arcY - 2, (int)arcX + 3, (int)arcY + 3)
            .ShouldBeLessThan((byte)120);

        // 弧外侧标签底
        DarkestRed(decoded, 122, 104, 154, 120).ShouldBeLessThan((byte)120);
    }

    [AvaloniaFact]
    public void 截图窗口_角度两次拖拽提交且Esc只清草稿()
    {
        var window = CreateWindowWithSelection();
        window.AngleToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 第一条边：只存草稿
        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 200));
        window.MouseUp(new Point(250, 200), MouseButton.Left);

        window.Notes.ShouldBeEmpty();
        window.AngleHint.IsVisible.ShouldBeTrue();

        // 第二条边：提交
        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(150, 120));
        window.MouseUp(new Point(150, 120), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<AngleAnnotation>().Label.ShouldBe("90°");
        window.AngleHint.IsVisible.ShouldBeFalse();

        // 再开一个草稿，Esc 只清草稿，不关窗口、不退工具
        window.MouseDown(new Point(200, 220), MouseButton.Left);
        window.MouseMove(new Point(260, 220));
        window.MouseUp(new Point(260, 220), MouseButton.Left);
        window.AngleHint.IsVisible.ShouldBeTrue();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        window.AngleHint.IsVisible.ShouldBeFalse();
        window.AngleToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.Notes.Count.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_角度第二条边太短时保留草稿()
    {
        var window = CreateWindowWithSelection();
        window.AngleToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 200));
        window.MouseUp(new Point(250, 200), MouseButton.Left);
        window.AngleHint.IsVisible.ShouldBeTrue();

        // 第二条边零长度：不提交，草稿保留
        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseUp(new Point(150, 200), MouseButton.Left);

        window.Notes.ShouldBeEmpty();
        window.AngleHint.IsVisible.ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_角度切工具会清掉草稿()
    {
        var window = CreateWindowWithSelection();
        window.AngleToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 200));
        window.MouseUp(new Point(250, 200), MouseButton.Left);
        window.AngleHint.IsVisible.ShouldBeTrue();

        window.KeyPress(Key.L, RawInputModifiers.None, PhysicalKey.L, null);

        window.AngleHint.IsVisible.ShouldBeFalse();
        window.LineToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_重新框选会清掉角度草稿()
    {
        var window = CreateWindowWithSelection();
        window.AngleToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 200), MouseButton.Left);
        window.MouseMove(new Point(250, 200));
        window.MouseUp(new Point(250, 200), MouseButton.Left);
        window.AngleHint.IsVisible.ShouldBeTrue();

        // 在选区外重新拉框：草稿必须跟着旧选区一起清掉
        window.MouseDown(new Point(600, 450), MouseButton.Left);
        window.MouseMove(new Point(700, 550));
        window.MouseUp(new Point(700, 550), MouseButton.Left);

        window.AngleHint.IsVisible.ShouldBeFalse();
        window.Notes.ShouldBeEmpty();

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_ShiftG切到角度()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.G, RawInputModifiers.Shift, PhysicalKey.G, null);

        window.AngleToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.WidthGroup.IsVisible.ShouldBeTrue();
        window.DashGroup.IsVisible.ShouldBeFalse();
        window.OpacityGroup.IsVisible.ShouldBeFalse();

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
